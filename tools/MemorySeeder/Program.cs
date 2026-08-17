// MemorySeeder — one-shot loader.
// Reads db/seed-corpus.json, embeds each item with Voyage AI (voyage-3.5),
// and inserts it into CockroachDB's memory_items table (vector-indexed).
// Usage:  COCKROACH_CONN=... VOYAGE_API_KEY=... dotnet run --project tools/MemorySeeder db/seed-corpus.json

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Pgvector;

var corpusPath = args.Length > 0 ? args[0] : "db/seed-corpus.json";
var conn = Environment.GetEnvironmentVariable("COCKROACH_CONN")
    ?? throw new InvalidOperationException("Set COCKROACH_CONN");
var voyageKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new InvalidOperationException("Set VOYAGE_API_KEY (get one free at voyageai.com)");

var items = JsonSerializer.Deserialize<List<SeedItem>>(await File.ReadAllTextAsync(corpusPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var dsb = new NpgsqlDataSourceBuilder(NormalizeConn(conn));
dsb.UseVector();
await using var db = dsb.Build();

using var http = new HttpClient { BaseAddress = new Uri("https://api.voyageai.com/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", voyageKey);

foreach (var item in items)
{
    var body = new JsonObject
    {
        ["input"] = new JsonArray { $"{item.Title}\n{item.Content}" },
        ["model"] = "voyage-3.5"
    };
    var response = await http.PostAsJsonAsync("v1/embeddings", body);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"Voyage error {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    var json = await response.Content.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidOperationException("Empty Voyage response");
    var arr = json["data"]![0]!["embedding"]!.AsArray();
    var embedding = new float[arr.Count];
    for (var i = 0; i < arr.Count; i++) embedding[i] = arr[i]!.GetValue<float>();

    await using var cmd = db.CreateCommand("""
        INSERT INTO memory_items (kind, service, title, content, embedding)
        VALUES ($1, $2, $3, $4, $5)
        """);
    cmd.Parameters.AddWithValue(item.Kind);
    cmd.Parameters.AddWithValue(item.Service);
    cmd.Parameters.AddWithValue(item.Title);
    cmd.Parameters.AddWithValue(item.Content);
    cmd.Parameters.AddWithValue(new Vector(embedding));
    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"✓ remembered: {item.Title}");
}

Console.WriteLine($"Seeded {items.Count} memories.");

// Accepts both keyword format and postgresql:// URLs (as provided by the CockroachDB console).
static string NormalizeConn(string raw)
{
    if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return raw;
    var uri = new Uri(raw);
    var ui = uri.UserInfo.Split(':', 2);
    var ssl = raw.Contains("sslmode=require") ? "Require" : "VerifyFull";
    return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 26257)};Database={uri.AbsolutePath.TrimStart('/')};Username={Uri.UnescapeDataString(ui[0])};Password={(ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : "")};SSL Mode={ssl}";
}

record SeedItem(string Kind, string Service, string Title, string Content);
