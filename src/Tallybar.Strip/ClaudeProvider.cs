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

    // Recycle pooled connections so a server-dropped idle connection can't fail the next
    // poll (a common cause of intermittent "stale" in a long-running app).
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public string Id => "claude";
    public string DisplayName => "Claude";
    public bool IsConfigured => File.Exists(CredentialsPath);

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        // Off the UI thread — the read may briefly sleep to ride out a CLI token rewrite.
        string token = await Task.Run(ReadAccessToken, ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("anthropic-beta", BetaHeader);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

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
        //
        // The CLI rewrites this file when it refreshes the token; reading mid-write throws
        // an IOException (locked) or JsonException (truncated). Those are transient, so
        // retry a few times before declaring an auth failure — otherwise one unlucky read
        // wrongly shows "sign-in needed" until the app restarts.
        Exception? transient = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                transient = e;
                Thread.Sleep(120);
                continue;
            }

            using (doc)
            {
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
            throw new UnauthorizedAccessException("No Claude OAuth token found.");
        }
        throw new UnauthorizedAccessException("Claude credentials unreadable.", transient);
    }
}
