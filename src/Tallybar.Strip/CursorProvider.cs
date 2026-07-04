using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tallybar;

/// <summary>
/// Cursor usage via the access token the Cursor app stores locally in its SQLite state
/// (%APPDATA%\Cursor\User\globalStorage\state.vscdb). No token of our own is stored.
/// </summary>
public sealed class CursorProvider : IProvider
{
    private const string UsageUrl = "https://cursor.com/api/usage-summary";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public bool IsConfigured => File.Exists(StatePath);

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        string jwt = ReadAccessToken() ?? throw new UnauthorizedAccessException("No Cursor session — sign in to Cursor.");
        string? sub = ProviderUtil.JwtClaim(jwt, "sub");
        string userId = sub?.Split('|').Last() ?? throw new UnauthorizedAccessException("Cursor token has no subject.");
        string cookie = $"WorkosCursorSessionToken={userId}%3A%3A{jwt}";

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Add("Cookie", cookie);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Cursor session rejected.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        DateTimeOffset? reset = null;
        if (root.TryGetProperty("billingCycleEnd", out JsonElement bce) &&
            DateTimeOffset.TryParse(bce.GetString(), out var d))
            reset = d;

        double used = double.NaN;
        if (root.TryGetProperty("individualUsage", out JsonElement iu) && iu.ValueKind == JsonValueKind.Object)
        {
            if (iu.TryGetProperty("plan", out JsonElement plan) && plan.ValueKind == JsonValueKind.Object)
            {
                if (plan.TryGetProperty("totalPercentUsed", out JsonElement tp) && tp.ValueKind == JsonValueKind.Number)
                    used = Math.Clamp(tp.GetDouble() / 100.0, 0, 1);
                else if (plan.TryGetProperty("used", out JsonElement u) && u.ValueKind == JsonValueKind.Number &&
                         plan.TryGetProperty("limit", out JsonElement l) && l.ValueKind == JsonValueKind.Number &&
                         l.GetDouble() > 0)
                    used = Math.Clamp(u.GetDouble() / l.GetDouble(), 0, 1);
            }
        }

        if (double.IsNaN(used))
            throw new InvalidDataException("No Cursor usage figure found.");
        return [new UsageSnapshot("cursor", "monthly", used, reset, now, FetchStatus.Ok)];
    }

    private static string? ReadAccessToken()
    {
        try
        {
            // Read-only + shared cache so we don't fight Cursor's own handle.
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = StatePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1;";
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            throw new UnauthorizedAccessException("Cursor state database unreadable.", e);
        }
    }
}
