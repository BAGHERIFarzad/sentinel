using System.Web;

namespace Sentinel.Core.Infra;

/// <summary>
/// Accepts both keyword format ("Host=...;Port=...") and the postgresql://
/// URL format that CockroachDB Cloud's console provides, normalizing the
/// latter to the keyword form Npgsql understands.
/// </summary>
public static class ConnectionString
{
    public static string Normalize(string raw)
    {
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return raw;

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 26257;

        var sslMode = "VerifyFull";
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var rawSsl = query.Get("sslmode");
        if (!string.IsNullOrEmpty(rawSsl))
            sslMode = rawSsl.ToLowerInvariant() switch
            {
                "verify-full" => "VerifyFull",
                "verify-ca" => "VerifyCA",
                "require" => "Require",
                "prefer" => "Prefer",
                "disable" => "Disable",
                _ => "VerifyFull"
            };

        return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode={sslMode}";
    }
}
