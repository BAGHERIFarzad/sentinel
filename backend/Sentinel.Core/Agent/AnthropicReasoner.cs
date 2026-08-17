using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Core.Agent;

public sealed record ToolCall(string Name, string InputJson, string ToolUseId);

public sealed record ReasonerReply(string? Text, List<ToolCall> ToolCalls)
{
    public bool WantsTool => ToolCalls.Count > 0;
}

/// <summary>
/// Calls Claude directly via the Anthropic Messages API instead of through
/// Bedrock's Converse API. Handles parallel tool use: Claude Sonnet 4.5 may
/// emit multiple tool_use blocks in a single turn, and Anthropic's API
/// requires ALL of them to receive a tool_result in the very next message —
/// so callers must execute every ToolCall in the reply and submit all
/// results together via AppendToolResults before the next ConverseAsync.
/// </summary>
public sealed class AnthropicReasoner
{
    private readonly HttpClient _http;
    private readonly string _modelId;

    public string ModelId => _modelId;

    public AnthropicReasoner(HttpClient http, SentinelOptions options)
    {
        _http = http;
        _modelId = options.AnthropicModelId;
        _http.BaseAddress = new Uri("https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Add("x-api-key", options.AnthropicApiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ReasonerReply> ConverseAsync(
        string systemPrompt, List<JsonObject> history, List<JsonObject> tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _modelId,
            ["max_tokens"] = 1500,
            ["temperature"] = 0.2,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray(history.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)t.DeepClone()).ToArray())
        };

        var response = await AnthropicRetry.RunAsync(() => _http.PostAsJsonAsync("v1/messages", body, ct), ct);

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Anthropic API error {(int)response.StatusCode}: {errText}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty response from Anthropic API");

        // Append the assistant's turn (exactly as Anthropic returned it) to history.
        history.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = json["content"]!.DeepClone()
        });

        string? text = null;
        var toolCalls = new List<ToolCall>();
        foreach (var block in json["content"]!.AsArray())
        {
            var type = block!["type"]!.GetValue<string>();
            if (type == "text") text = block["text"]!.GetValue<string>();
            if (type == "tool_use")
            {
                toolCalls.Add(new ToolCall(
                    block["name"]!.GetValue<string>(),
                    block["input"]!.ToJsonString(),
                    block["id"]!.GetValue<string>()));
            }
        }

        return new ReasonerReply(text, toolCalls);
    }

    /// <summary>Appends ALL tool results from one turn as a single user message —
    /// required whenever the prior assistant turn contained more than one tool_use.</summary>
    public static void AppendToolResults(List<JsonObject> history, IEnumerable<(string ToolUseId, string ResultJson)> results)
    {
        history.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = r.ToolUseId,
                ["content"] = r.ResultJson
            }).ToArray())
        });
    }
}

/// <summary>Retries through Anthropic 429s (rate limit) with backoff.</summary>
public static class AnthropicRetry
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
            Console.WriteLine($"[anthropic] rate limited — retry {attempt}/{maxAttempts - 1} in {delay}s");
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
        }
    }
}
