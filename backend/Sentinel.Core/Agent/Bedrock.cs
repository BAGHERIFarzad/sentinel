using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Sentinel.Core.Agent;

/// <summary>Retries Bedrock calls through throttling with exponential backoff —
/// new AWS accounts have low burst quotas, and an agent must ride that out.</summary>
public static class BedrockRetry
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken ct, int maxAttempts = 6)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await call(); }
            catch (Amazon.BedrockRuntime.Model.ThrottlingException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); // 2,4,8,16,32s
            }
        }
    }
}

public sealed class SentinelOptions
{
    public string ReasonerModelId { get; set; } = "anthropic.claude-3-5-sonnet-20240620-v1:0";
    public string EmbeddingModelId { get; set; } = "amazon.titan-embed-text-v2:0";
    public string? PostmortemBucket { get; set; }
    public int MaxAgentSteps { get; set; } = 8;

    // Direct Anthropic API (bypasses AWS Bedrock entirely for reasoning) — set via
    // ANTHROPIC_API_KEY env var. Model list: https://docs.anthropic.com/en/docs/about-claude/models
    public string AnthropicApiKey { get; set; } = "";
    public string AnthropicModelId { get; set; } = "claude-sonnet-4-5-20250929";

    // Voyage AI embeddings (bypasses AWS Bedrock entirely for memory recall) —
    // set via VOYAGE_API_KEY env var. Get one free at voyageai.com.
    public string VoyageApiKey { get; set; } = "";
}

/// <summary>Amazon Bedrock — Titan Text Embeddings V2 (1024-dim).</summary>
public sealed class EmbeddingService(IAmazonBedrockRuntime bedrock, SentinelOptions options)
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { inputText = text, dimensions = 1024, normalize = true });
        var response = await BedrockRetry.RunAsync(() => bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = options.EmbeddingModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body))
        }, ct), ct);

        using var doc = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct);
        var arr = doc.RootElement.GetProperty("embedding");
        var embedding = new float[arr.GetArrayLength()];
        var i = 0;
        foreach (var v in arr.EnumerateArray()) embedding[i++] = v.GetSingle();
        return embedding;
    }
}

