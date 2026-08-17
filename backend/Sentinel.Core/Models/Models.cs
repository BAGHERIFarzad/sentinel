namespace Sentinel.Core.Models;

public sealed record Incident(
    Guid Id,
    string Title,
    string Service,
    string Severity,
    string Status,
    string Signature,
    string? Resolution,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record IncidentEvent(
    Guid Id,
    Guid IncidentId,
    string Kind,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record AgentAction(
    Guid Id,
    Guid? IncidentId,
    int Step,
    string Tool,
    string InputJson,
    string? OutputJson,
    string ModelId,
    int LatencyMs,
    DateTimeOffset CreatedAt);

public sealed record MemoryMatch(
    Guid Id,
    string Kind,
    string? Service,
    string Title,
    string Content,
    double Distance);

public sealed record AlertPayload(
    string Title,
    string Service,
    string Severity,
    string Description,
    Dictionary<string, string>? Metrics);

public sealed record ClusterCheck(
    Guid Id,
    string CheckKind,
    string ResultJson,
    bool Healthy,
    DateTimeOffset CreatedAt);
