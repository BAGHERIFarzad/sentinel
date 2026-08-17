using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Infra;
using Sentinel.Core.Memory;
using Sentinel.Core.Models;

namespace Sentinel.Core.Agent;

/// <summary>
/// Sentinel's core loop. For each incident:
///   1. Persist the incident (transactional state in CockroachDB).
///   2. Recall similar past incidents/runbooks (distributed vector index).
///   3. Let Claude (direct Anthropic API) reason over live telemetry + recalled
///      memory, calling tools until the incident is resolved or escalated.
///      Claude may call multiple tools in one turn (parallel tool use) — every
///      call in a turn is executed and ALL results are returned together,
///      exactly as Anthropic's API requires.
///   4. Record EVERY action in an immutable audit trail.
///   5. On resolution: write a postmortem, archive it to S3, embed it, and
///      store it back into memory — the agent literally learns from the outage.
/// Before mutating memory in risky ways, the agent snapshots its own memory
/// layer via the ccloud CLI. Memory is treated as production infrastructure.
/// </summary>
public sealed class AgentLoop(
    MemoryStore memory,
    VoyageEmbeddingService embeddings,
    AnthropicReasoner reasoner,
    CcloudService ccloud,
    TelemetryProvider telemetry,
    PostmortemArchiver archiver,
    SentinelOptions options,
    ILogger<AgentLoop> logger)
{
    public async Task<Incident> HandleAlertAsync(AlertPayload alert, CancellationToken ct)
    {
        var signature = Normalize(alert);
        var incident = await memory.OpenIncidentAsync(alert, signature, ct);
        logger.LogInformation("Incident {Id} opened for {Service}: {Title}", incident.Id, alert.Service, alert.Title);

        var step = 0;
        var sw = new Stopwatch();

        // ── Step 1: semantic recall from lifelong memory ────────────────────
        sw.Restart();
        var queryEmbedding = await embeddings.EmbedAsync($"{alert.Title}\n{alert.Description}", ct);
        var recalled = await memory.RecallAsync(queryEmbedding, topK: 4, ct);
        sw.Stop();
        await memory.RecordActionAsync(incident.Id, ++step, "consult_memory",
            JsonSerializer.Serialize(new { query = alert.Title }),
            JsonSerializer.Serialize(recalled.Select(m => new { m.Title, m.Kind, similarity = Math.Round(1 - m.Distance, 3) })),
            reasoner.ModelId, (int)sw.ElapsedMilliseconds, ct);

        await memory.TransitionAsync(incident.Id, "diagnosing",
            $"Recalled {recalled.Count} related memories", ct);

        // ── Step 2+: reasoning loop with (possibly parallel) tools ──────────
        var history = new List<JsonObject>
        {
            new()
            {
                ["role"] = "user",
                ["content"] = BuildBriefing(alert, recalled)
            }
        };
        var tools = ToolDefinitions();

        var resolved = false;
        while (!resolved && step < options.MaxAgentSteps)
        {
            sw.Restart();
            var reply = await reasoner.ConverseAsync(SystemPrompt, history, tools, ct);
            sw.Stop();

            if (!reply.WantsTool)
            {
                await memory.AppendEventAsync(incident.Id, "note", new { text = reply.Text ?? "(no text)" }, ct);
                break;
            }

            // Execute every tool call from this turn and collect ALL results —
            // Anthropic requires a tool_result for every tool_use in the same
            // next message, even when the model called several tools at once.
            var turnResults = new List<(string ToolUseId, string ResultJson)>();

            foreach (var call in reply.ToolCalls)
            {
                var resultJson = await ExecuteToolAsync(incident.Id, alert, call.Name, call.InputJson, ct);
                turnResults.Add((call.ToolUseId, resultJson));

                await memory.RecordActionAsync(incident.Id, ++step, call.Name, call.InputJson, resultJson,
                    reasoner.ModelId, (int)sw.ElapsedMilliseconds, ct);

                if (call.Name is "resolve_incident" or "escalate") resolved = true;
            }

            AnthropicReasoner.AppendToolResults(history, turnResults);
        }

        if (!resolved)
        {
            await memory.TransitionAsync(incident.Id, "escalated", "Max agent steps reached — escalating to a human", ct);
        }

        return incident with { };
    }

    private async Task<string> ExecuteToolAsync(Guid incidentId, AlertPayload alert, string toolName, string input, CancellationToken ct)
    {
        switch (toolName)
        {
            case "query_metrics":
                return telemetry.Query(alert.Service, input);

            case "consult_memory":
            {
                var q = JsonDocument.Parse(input).RootElement.TryGetProperty("query", out var qEl)
                    ? qEl.GetString() ?? alert.Title : alert.Title;
                var emb = await embeddings.EmbedAsync(q, ct);
                var more = await memory.RecallAsync(emb, 3, ct);
                return JsonSerializer.Serialize(more.Select(m => new { m.Title, m.Content, similarity = Math.Round(1 - m.Distance, 3) }));
            }

            case "apply_mitigation":
            {
                // Before any mitigation that touches state, snapshot the memory
                // layer itself: the agent manages its own database via ccloud.
                var snapshot = await ccloud.SnapshotClusterStateAsync(ct);
                await memory.RecordClusterCheckAsync("snapshot", snapshot, snapshot.Ok, ct);
                await memory.TransitionAsync(incidentId, "mitigating", "Applying mitigation (memory snapshotted first)", ct);
                var action = JsonDocument.Parse(input).RootElement.GetProperty("action").GetString()!;
                await memory.AppendEventAsync(incidentId, "mitigation", new { action }, ct);
                return JsonSerializer.Serialize(new
                {
                    applied = true, action,
                    cluster_state = snapshot.Ok ? "state snapshot captured via ccloud before mutation" : "ccloud unavailable (logged)",
                    effect = telemetry.SimulateMitigation(alert.Service, action)
                });
            }

            case "resolve_incident":
            {
                var root = JsonDocument.Parse(input).RootElement;
                var rootCause = root.GetProperty("root_cause").GetString()!;
                var resolution = root.GetProperty("resolution").GetString()!;
                await memory.SetResolutionAsync(incidentId, resolution, ct);
                await memory.TransitionAsync(incidentId, "resolved", rootCause, ct);

                var postmortem = BuildPostmortem(alert, rootCause, resolution);
                var s3Uri = await archiver.ArchiveAsync(incidentId, postmortem, ct);
                var pmEmbedding = await embeddings.EmbedAsync(postmortem, ct);
                await memory.RememberAsync("postmortem", incidentId, alert.Service,
                    $"{DateTime.UtcNow:yyyy-MM-dd} — {alert.Service}: {rootCause}", postmortem, pmEmbedding, ct);

                return JsonSerializer.Serialize(new { resolved = true, postmortem_archived = s3Uri ?? "s3 disabled" });
            }

            case "escalate":
                await memory.TransitionAsync(incidentId, "escalated",
                    JsonDocument.Parse(input).RootElement.GetProperty("reason").GetString() ?? "unspecified", ct);
                return JsonSerializer.Serialize(new { escalated = true });

            default:
                return JsonSerializer.Serialize(new { error = $"unknown tool {toolName}" });
        }
    }

    private const string SystemPrompt = """
        You are Sentinel, an autonomous SRE incident-response agent. You have lifelong
        memory of every incident you have ever handled, stored in CockroachDB and
        provided to you as recalled context. Follow this doctrine strictly:
        1. Trust your memory: if a recalled postmortem matches the current failure
           signature, prefer the mitigation that WORKED there and avoid the ones
           that explicitly did NOT work.
        2. Verify before acting: use query_metrics to confirm your hypothesis, but
           don't over-check — 2-3 metric checks are usually enough before you act.
        3. Act minimally: one mitigation at a time via apply_mitigation, then verify
           with 1-2 follow-up metric checks. Once metrics confirm recovery, call
           resolve_incident promptly — don't keep re-checking metrics that already
           look healthy.
        4. If metrics recover, call resolve_incident with a clear root_cause and
           resolution. If you cannot resolve within your step budget or the blast
           radius is unclear, call escalate.
        Be concise. Never invent metrics — always query them.
        """;

    private static string BuildBriefing(AlertPayload alert, IReadOnlyList<MemoryMatch> recalled)
    {
        var memories = recalled.Count == 0
            ? "(no relevant memories — this failure signature is new to you)"
            : string.Join("\n\n", recalled.Select((m, i) =>
                $"[Memory {i + 1} | {m.Kind} | similarity {1 - m.Distance:0.00}] {m.Title}\n{m.Content}"));

        return $"""
            NEW ALERT
            Service: {alert.Service}
            Severity: {alert.Severity}
            Title: {alert.Title}
            Description: {alert.Description}

            RECALLED FROM YOUR LIFELONG MEMORY (CockroachDB vector index):
            {memories}

            Diagnose and resolve this incident.
            """;
    }

    private static string BuildPostmortem(AlertPayload alert, string rootCause, string resolution) => $"""
        Symptom: {alert.Title}. {alert.Description}
        Service: {alert.Service} (severity {alert.Severity})
        Root cause: {rootCause}
        Detection signature: {alert.Title}
        Mitigation that worked: {resolution}
        """;

    private static string Normalize(AlertPayload a) =>
        $"{a.Service}:{a.Title}".ToLowerInvariant().Replace("  ", " ").Trim();

    private static List<JsonObject> ToolDefinitions() =>
    [
        AnthropicTool("query_metrics", "Query live telemetry for the affected service.",
            new { type = "object", properties = new { metric = new { type = "string", description = "one of: latency_p99, error_rate, cpu, memory, db_connections, recent_deploys" } }, required = new[] { "metric" } }),
        AnthropicTool("consult_memory", "Search your lifelong memory (past postmortems and runbooks) with a free-text query.",
            new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } }),
        AnthropicTool("apply_mitigation", "Apply exactly one mitigation action. Cluster state is automatically snapshotted via ccloud before this runs.",
            new { type = "object", properties = new { action = new { type = "string", description = "e.g. 'rollback deploy v2.14.3', 'enable flag cart_cache_lru', 'recreate covering index'" } }, required = new[] { "action" } }),
        AnthropicTool("resolve_incident", "Mark the incident resolved. Triggers postmortem generation, S3 archival, and memory writeback.",
            new { type = "object", properties = new { root_cause = new { type = "string" }, resolution = new { type = "string" } }, required = new[] { "root_cause", "resolution" } }),
        AnthropicTool("escalate", "Hand the incident to a human with a reason.",
            new { type = "object", properties = new { reason = new { type = "string" } }, required = new[] { "reason" } })
    ];

    private static JsonObject AnthropicTool(string name, string description, object schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = JsonNode.Parse(JsonSerializer.Serialize(schema))
    };
}
