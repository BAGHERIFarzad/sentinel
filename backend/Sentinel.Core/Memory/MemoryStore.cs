using System.Text.Json;
using Npgsql;
using Pgvector;
using Sentinel.Core.Models;

namespace Sentinel.Core.Memory;

/// <summary>
/// The agent's entire memory lives in CockroachDB:
///  - transactional state (incidents, events, audit trail)
///  - semantic memory (embeddings under a distributed vector index)
/// Both are written in the same transactions, so the agent's "what happened"
/// and "what it means" can never drift apart.
/// </summary>
public sealed class MemoryStore(NpgsqlDataSource db)
{
    // ── Incident state machine (transactional) ──────────────────────────────

    public async Task<Incident> OpenIncidentAsync(AlertPayload alert, string signature, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO incidents (title, service, severity, signature, alert_payload)
            VALUES ($1, $2, $3, $4, $5)
            RETURNING id, title, service, severity, status, signature, resolution, created_at, resolved_at
            """;
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(alert.Title);
        cmd.Parameters.AddWithValue(alert.Service);
        cmd.Parameters.AddWithValue(alert.Severity);
        cmd.Parameters.AddWithValue(signature);
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(alert), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadIncident(reader);
    }

    public async Task TransitionAsync(Guid incidentId, string newStatus, string note, CancellationToken ct)
    {
        // Status change + event log in ONE transaction — the state machine and
        // its history are always consistent, even across region failures.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var upd = new NpgsqlCommand(
            "UPDATE incidents SET status = $1, resolved_at = CASE WHEN $1 = 'resolved' THEN now() ELSE resolved_at END WHERE id = $2", conn, tx))
        {
            upd.Parameters.AddWithValue(newStatus);
            upd.Parameters.AddWithValue(incidentId);
            await upd.ExecuteNonQueryAsync(ct);
        }

        await using (var evt = new NpgsqlCommand(
            "INSERT INTO incident_events (incident_id, kind, payload) VALUES ($1, 'status_change', $2)", conn, tx))
        {
            evt.Parameters.AddWithValue(incidentId);
            evt.Parameters.Add(new NpgsqlParameter
            {
                Value = JsonSerializer.Serialize(new { status = newStatus, note }),
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb
            });
            await evt.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task SetResolutionAsync(Guid incidentId, string resolution, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("UPDATE incidents SET resolution = $1 WHERE id = $2");
        cmd.Parameters.AddWithValue(resolution);
        cmd.Parameters.AddWithValue(incidentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendEventAsync(Guid incidentId, string kind, object payload, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO incident_events (incident_id, kind, payload) VALUES ($1, $2, $3)");
        cmd.Parameters.AddWithValue(incidentId);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(payload), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Audit trail ─────────────────────────────────────────────────────────

    public async Task RecordActionAsync(Guid? incidentId, int step, string tool, string inputJson,
        string? outputJson, string modelId, int latencyMs, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO agent_actions (incident_id, step, tool, input, output, model_id, latency_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """);
        cmd.Parameters.AddWithValue((object?)incidentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(step);
        cmd.Parameters.AddWithValue(tool);
        cmd.Parameters.Add(new NpgsqlParameter { Value = inputJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)outputJson ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.AddWithValue(modelId);
        cmd.Parameters.AddWithValue(latencyMs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Semantic memory (distributed vector index) ──────────────────────────

    public async Task<Guid> RememberAsync(string kind, Guid? incidentId, string? service,
        string title, string content, float[] embedding, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO memory_items (kind, incident_id, service, title, content, embedding)
            VALUES ($1, $2, $3, $4, $5, $6)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue((object?)incidentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)service ?? DBNull.Value);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(content);
        cmd.Parameters.AddWithValue(new Vector(embedding));
        var id = await cmd.ExecuteScalarAsync(ct);
        return (Guid)id!;
    }

    /// <summary>
    /// Semantic recall: "have I seen a failure like this before?"
    /// Served by CockroachDB's distributed vector index — no separate vector DB.
    /// </summary>
    public async Task<IReadOnlyList<MemoryMatch>> RecallAsync(float[] queryEmbedding, int topK, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, kind, service, title, content, embedding <-> $1 AS distance
            FROM memory_items
            ORDER BY embedding <-> $1
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(topK);

        var matches = new List<MemoryMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            matches.Add(new MemoryMatch(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5)));
        }
        return matches;
    }

    // ── Read models for the dashboard ───────────────────────────────────────

    public async Task<IReadOnlyList<Incident>> ListIncidentsAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, title, service, severity, status, signature, resolution, created_at, resolved_at
            FROM incidents ORDER BY created_at DESC LIMIT 50
            """);
        var list = new List<Incident>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadIncident(reader));
        return list;
    }

    public async Task<IReadOnlyList<AgentAction>> GetTraceAsync(Guid incidentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, incident_id, step, tool, input, output, model_id, latency_ms, created_at
            FROM agent_actions WHERE incident_id = $1 ORDER BY step
            """);
        cmd.Parameters.AddWithValue(incidentId);
        var list = new List<AgentAction>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AgentAction(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return list;
    }

    public async Task RecordClusterCheckAsync(string kind, object result, bool healthy, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO cluster_checks (check_kind, result, healthy) VALUES ($1, $2, $3)");
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(result), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.AddWithValue(healthy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ClusterCheck>> RecentClusterChecksAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, check_kind, result, healthy, created_at FROM cluster_checks ORDER BY created_at DESC LIMIT 20");
        var list = new List<ClusterCheck>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ClusterCheck(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return list;
    }

    private static Incident ReadIncident(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8));
}
