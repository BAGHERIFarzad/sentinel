using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SNSEvents;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AlertIngest;

/// <summary>
/// Serverless entry point (AWS Lambda). CloudWatch Alarms publish to SNS,
/// SNS triggers this function, and it normalizes the alarm into a Sentinel
/// AlertPayload before forwarding it to the agent's API. This is the
/// "agents spawn autonomously" edge: no human is in the loop between an
/// alarm firing and the agent starting to reason.
/// </summary>
public class Function
{
    private static readonly HttpClient Http = new();
    private static readonly string SentinelApi =
        Environment.GetEnvironmentVariable("SENTINEL_API_URL")
        ?? throw new InvalidOperationException("SENTINEL_API_URL not set");

    public async Task Handler(SNSEvent evt, ILambdaContext context)
    {
        foreach (var record in evt.Records)
        {
            try
            {
                var alarm = JsonDocument.Parse(record.Sns.Message).RootElement;
                var payload = new
                {
                    title = alarm.TryGetProperty("AlarmName", out var n) ? n.GetString() : "CloudWatch alarm",
                    service = alarm.TryGetProperty("Trigger", out var t) &&
                              t.TryGetProperty("Dimensions", out var d) && d.GetArrayLength() > 0
                        ? d[0].GetProperty("value").GetString() : "unknown",
                    severity = "SEV2",
                    description = alarm.TryGetProperty("NewStateReason", out var r) ? r.GetString() : record.Sns.Message,
                    metrics = (object?)null
                };

                var response = await Http.PostAsync($"{SentinelApi}/api/alerts",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                context.Logger.LogInformation($"Forwarded alarm → Sentinel: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Failed to forward alarm: {ex.Message}");
                throw; // let Lambda retry / DLQ handle it
            }
        }
    }
}
