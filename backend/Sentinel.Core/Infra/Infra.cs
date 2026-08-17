using System.Diagnostics;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Sentinel.Core.Agent;

namespace Sentinel.Core.Infra;

public sealed record CcloudResult(bool Ok, string Command, string Output);

/// <summary>
/// The agent treats its own memory layer as production infrastructure and
/// manages it through the agent-ready ccloud CLI: JSON output on every command,
/// scoped to a service account with granular RBAC.
/// </summary>
public sealed class CcloudService(ILogger<CcloudService> logger)
{
    public string ClusterId { get; init; } =
        Environment.GetEnvironmentVariable("CCLOUD_CLUSTER_ID") ?? "";

    public bool Enabled => !string.IsNullOrEmpty(ClusterId);

    public Task<CcloudResult> GetClusterHealthAsync(CancellationToken ct) =>
        RunAsync($"cluster info {ClusterId} -o json", ct);

    /// <summary>
    /// Captures cluster state via ccloud before a risky mitigation. A triage
    /// agent gets a least-privilege, read-only role (per CockroachDB's agent
    /// security model) — it can inspect cluster state but not create backups
    /// or modify configuration. So "snapshot before mutating" here means
    /// reading and recording current cluster state via ccloud, not triggering
    /// a backup operation the agent's role shouldn't have anyway.
    /// </summary>
    public Task<CcloudResult> SnapshotClusterStateAsync(CancellationToken ct) =>
        RunAsync($"cluster info {ClusterId} -o json", ct);

    private async Task<CcloudResult> RunAsync(string args, CancellationToken ct)
    {
        var command = $"ccloud {args}";
        if (!Enabled)
        {
            // Demo-safe fallback: no cluster configured → report and log honestly.
            return new CcloudResult(false, command, JsonSerializer.Serialize(new
            {
                skipped = true,
                reason = "CCLOUD_CLUSTER_ID not set — ccloud self-management disabled"
            }));
        }

        try
        {
            var psi = new ProcessStartInfo("ccloud", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var ok = process.ExitCode == 0;
            if (!ok) logger.LogWarning("ccloud failed: {Args} → {Err}", args, stderr);
            return new CcloudResult(ok, command, ok ? stdout : stderr);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ccloud invocation error");
            return new CcloudResult(false, command, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }
}

/// <summary>Archives postmortems as durable artifacts in Amazon S3.</summary>
public sealed class PostmortemArchiver(IAmazonS3 s3, SentinelOptions options, ILogger<PostmortemArchiver> logger)
{
    public async Task<string?> ArchiveAsync(Guid incidentId, string postmortem, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.PostmortemBucket)) return null;
        try
        {
            var key = $"postmortems/{DateTime.UtcNow:yyyy/MM}/{incidentId}.md";
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.PostmortemBucket,
                Key = key,
                ContentBody = postmortem,
                ContentType = "text/markdown"
            }, ct);
            return $"s3://{options.PostmortemBucket}/{key}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "S3 archive failed — postmortem still persisted in CockroachDB");
            return null;
        }
    }
}

/// <summary>
/// Deterministic demo telemetry. Each demo scenario has a scripted metric
/// profile so judges can reproduce the exact agent behavior shown in the video.
/// Swap this class for a CloudWatch/Prometheus client in production.
/// </summary>
public sealed class TelemetryProvider
{
    private readonly Dictionary<string, bool> _mitigated = new();

    public string Query(string service, string inputJson)
    {
        var metric = JsonDocument.Parse(inputJson).RootElement.TryGetProperty("metric", out var m)
            ? m.GetString() ?? "latency_p99" : "latency_p99";
        var healthy = _mitigated.TryGetValue(service, out var v) && v;

        object result = (service, metric, healthy) switch
        {
            (_, "recent_deploys", _) => new { deploys = new[] { new { version = "v2.14.3", minutes_ago = 22 }, new { version = "v2.14.2", minutes_ago = 1440 } } },
            ("api-gateway", "db_connections", false) => new { pool_in_use = 100, pool_max = 100, db_cpu_percent = 31, note = "pool saturated, database itself healthy" },
            ("api-gateway", "db_connections", true) => new { pool_in_use = 41, pool_max = 100, db_cpu_percent = 28 },
            ("checkout", "memory", false) => new { container_memory_mi = 508, limit_mi = 512, oom_kills_last_10m = 4, exit_code = 137 },
            ("checkout", "memory", true) => new { container_memory_mi = 340, limit_mi = 1024, oom_kills_last_10m = 0 },
            (_, "latency_p99", false) => new { latency_p99_ms = 4120, slo_ms = 800 },
            (_, "latency_p99", true) => new { latency_p99_ms = 240, slo_ms = 800 },
            (_, "error_rate", false) => new { error_rate_percent = 18.4 },
            (_, "error_rate", true) => new { error_rate_percent = 0.2 },
            (_, "cpu", _) => new { cpu_percent = healthy ? 34 : 78 },
            _ => new { note = "metric nominal" }
        };
        return JsonSerializer.Serialize(result);
    }

    public string SimulateMitigation(string service, string action)
    {
        _mitigated[service] = true;
        return $"'{action}' applied; metrics recovering — verify with query_metrics";
    }
}
