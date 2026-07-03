using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// Claude usage via the OAuth session that Claude Code already maintains on this machine.
/// Reads the access token from ~/.claude/.credentials.json and queries the usage endpoint.
/// No secrets of our own are stored; nothing is written back.
/// </summary>
public sealed class ClaudeProvider : IProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Id => "claude";
    public string DisplayName => "Claude";
    public bool IsConfigured => File.Exists(CredentialsPath);

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        string token = ReadAccessToken();

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("anthropic-beta", BetaHeader);

        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Claude OAuth token rejected.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // The endpoint has emitted both camelCase and snake_case key styles; accept either.
        var snaps = new List<UsageSnapshot>();
        AddWindow(snaps, doc.RootElement, ["fiveHour", "five_hour"], "session", now);
        AddWindow(snaps, doc.RootElement, ["sevenDay", "seven_day"], "weekly", now);
        AddWindow(snaps, doc.RootElement, ["sevenDayOpus", "seven_day_opus"], "weekly (Opus)", now);
        if (snaps.Count == 0)
            throw new InvalidDataException("Usage response had no recognizable windows.");
        return snaps;
    }

    private static void AddWindow(
        List<UsageSnapshot> snaps, JsonElement root, string[] keys, string label, DateTimeOffset now)
    {
        JsonElement w = default;
        bool found = false;
        foreach (string key in keys)
        {
            if (root.TryGetProperty(key, out w) && w.ValueKind == JsonValueKind.Object) { found = true; break; }
        }
        if (!found) return;

        double fraction = double.NaN;
        if (w.TryGetProperty("utilization", out JsonElement util) && util.ValueKind == JsonValueKind.Number)
        {
            double x = util.GetDouble();
            // Value may be a 0..1 fraction or a 0..100 percent; normalize to a fraction.
            fraction = Math.Clamp(x is > 0 and <= 1 ? x : x / 100.0, 0, 1);
        }

        DateTimeOffset? resetsAt = null;
        foreach (string rk in (string[])["resetsAt", "resets_at"])
        {
            if (w.TryGetProperty(rk, out JsonElement reset))
            {
                if (reset.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(reset.GetString(), out DateTimeOffset parsed))
                    resetsAt = parsed;
                else if (reset.ValueKind == JsonValueKind.Number) // epoch seconds
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
                break;
            }
        }

        if (!double.IsNaN(fraction))
            snaps.Add(new UsageSnapshot("claude", label, fraction, resetsAt, now, FetchStatus.Ok));
    }

    private static string ReadAccessToken()
    {
        // Deliberately no token refresh: the claude CLI owns this file and refreshes it
        // whenever it runs. If the token is expired we surface AuthError instead of
        // competing with the CLI over the credential file.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            // Token JSON is either wrapped in "claudeAiOauth" or at the top level.
            JsonElement oauth = doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement wrapped)
                ? wrapped : doc.RootElement;

            // expiresAt is epoch milliseconds; treat "expires within 5 min" as expired.
            if (oauth.TryGetProperty("expiresAt", out JsonElement exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64())
                    <= DateTimeOffset.UtcNow.AddMinutes(5))
                throw new UnauthorizedAccessException("Claude OAuth token expired — run `claude` to refresh.");

            if (oauth.TryGetProperty("accessToken", out JsonElement tok) &&
                tok.GetString() is { Length: > 0 } token)
                return token;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new UnauthorizedAccessException("Claude credentials unreadable.", e);
        }
        throw new UnauthorizedAccessException("No Claude OAuth token found.");
    }
}
