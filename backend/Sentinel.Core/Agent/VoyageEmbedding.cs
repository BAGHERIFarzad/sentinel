using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Core.Agent;

/// <summary>
/// Voyage AI — Anthropic's recommended embedding partner, called directly via
/// REST. Entirely separate from AWS Bedrock, so it sidesteps the account-wide
/// Bedrock token cap. voyage-3.5 defaults to 1024-dim output, which matches
/// the memory_items.embedding VECTOR(1024) column with no schema change.
/// </summary>
public sealed class VoyageEmbeddingService
{
    private readonly HttpClient _http;
    private const string Model = "voyage-3.5";

    public VoyageEmbeddingService(HttpClient http, SentinelOptions options)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://api.voyageai.com/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.VoyageApiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["input"] = new JsonArray { text },
            ["model"] = Model
        };

        var response = await VoyageRetry.RunAsync(() => _http.PostAsJsonAsync("v1/embeddings", body, ct), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Voyage API error {(int)response.StatusCode}: {errText}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty response from Voyage API");

        var arr = json["data"]![0]!["embedding"]!.AsArray();
        var embedding = new float[arr.Count];
        for (var i = 0; i < arr.Count; i++) embedding[i] = arr[i]!.GetValue<float>();
        return embedding;
    }
}

/// <summary>Retries through Voyage 429s (rate limit) with backoff.</summary>
public static class VoyageRetry
{
    public static async Task<HttpResponseMessage> RunAsync(
        Func<Task<HttpResponseMessage>> call, CancellationToken ct, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await call();
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= maxAttempts)
                return response;

            var delay = Math.Pow(2, attempt);
            Console.WriteLine($"[voyage] rate limited — retry {attempt}/{maxAttempts - 1} in {delay}s");
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }
}
