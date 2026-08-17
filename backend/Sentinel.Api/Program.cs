using Amazon.S3;
using Npgsql;
using Sentinel.Core.Agent;
using Sentinel.Core.Infra;
using Sentinel.Core.Memory;
using Sentinel.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
var options = new SentinelOptions();
builder.Configuration.GetSection("Sentinel").Bind(options);
options.PostmortemBucket ??= Environment.GetEnvironmentVariable("SENTINEL_S3_BUCKET");
options.AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("Set ANTHROPIC_API_KEY (get one at console.anthropic.com).");
options.VoyageApiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new InvalidOperationException("Set VOYAGE_API_KEY (get one free at voyageai.com).");
builder.Services.AddSingleton(options);

// CockroachDB — Postgres-compatible, so Npgsql just works. Vector support via Pgvector.
var connectionString = Environment.GetEnvironmentVariable("COCKROACH_CONN")
    ?? builder.Configuration.GetConnectionString("Cockroach")
    ?? throw new InvalidOperationException("Set COCKROACH_CONN to your CockroachDB Cloud connection string.");
var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString.Normalize(connectionString));
dataSourceBuilder.UseVector();
builder.Services.AddSingleton(dataSourceBuilder.Build());

// AWS S3 only — Bedrock is no longer used (reasoning via Anthropic API, embeddings via Voyage API)
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());

builder.Services.AddSingleton<MemoryStore>();
builder.Services.AddHttpClient<VoyageEmbeddingService>(); // direct Voyage API — bypasses Bedrock entirely for embeddings
builder.Services.AddHttpClient<AnthropicReasoner>(); // direct Anthropic API — bypasses Bedrock entirely for reasoning
builder.Services.AddSingleton<CcloudService>();
builder.Services.AddSingleton<TelemetryProvider>();
builder.Services.AddSingleton<PostmortemArchiver>();
builder.Services.AddSingleton<AgentLoop>();
builder.Services.AddHostedService<CcloudWatchdog>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// ── Endpoints ────────────────────────────────────────────────────────────────

// Alert intake — called by the AWS Lambda ingester (or directly for demos).
app.MapPost("/api/alerts", async (AlertPayload alert, AgentLoop agent, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var incident = await agent.HandleAlertAsync(alert, ct);
        return Results.Ok(new { incidentId = incident.Id });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Agent failed while handling alert");
        return Results.Problem(title: "Agent failed", detail: ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/incidents", async (MemoryStore store, CancellationToken ct) =>
    Results.Ok(await store.ListIncidentsAsync(ct)));

app.MapGet("/api/incidents/{id:guid}/trace", async (Guid id, MemoryStore store, CancellationToken ct) =>
    Results.Ok(await store.GetTraceAsync(id, ct)));

app.MapGet("/api/cluster/checks", async (MemoryStore store, CancellationToken ct) =>
    Results.Ok(await store.RecentClusterChecksAsync(ct)));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "sentinel" }));

app.Run();

/// <summary>
/// Background watchdog: every 60s the agent checks the health of its OWN memory
/// layer through the ccloud CLI and records the result. If its memory is
/// degraded, that is itself an incident.
/// </summary>
public sealed class CcloudWatchdog(CcloudService ccloud, MemoryStore store, ILogger<CcloudWatchdog> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var health = await ccloud.GetClusterHealthAsync(ct);
                await store.RecordClusterCheckAsync("health", health, health.Ok || !ccloud.Enabled, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watchdog cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }
}
