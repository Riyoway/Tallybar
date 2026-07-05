using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// Codex (OpenAI) usage via the session that the codex CLI maintains locally.
/// Reads the access token from ~/.codex/auth.json (or $CODEX_HOME/auth.json) and
/// queries the usage endpoint. Nothing is stored or written back.
/// </summary>
public sealed class CodexProvider : IProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static string AuthPath
    {
        get
        {
            string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string root = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : home;
            return Path.Combine(root, "auth.json");
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Id => "codex";
    public string DisplayName => "Codex";
    public bool IsConfigured => File.Exists(AuthPath);

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        (string token, string? accountId) = ReadCredentials();

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("Tallybar");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (accountId is not null)
            req.Headers.Add("ChatGPT-Account-Id", accountId);

        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Codex token rejected — run `codex` to re-authenticate.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var snaps = new List<UsageSnapshot>();
        if (doc.RootElement.TryGetProperty("rate_limit", out JsonElement rl))
        {
            AddWindow(snaps, rl, "primary_window", "session", now);
            AddWindow(snaps, rl, "secondary_window", "weekly", now);
        }
        if (snaps.Count == 0)
            throw new InvalidDataException("Usage response had no recognizable windows.");
        return snaps;
    }

    private static void AddWindow(
        List<UsageSnapshot> snaps, JsonElement rateLimit, string key, string label, DateTimeOffset now)
    {
        if (!rateLimit.TryGetProperty(key, out JsonElement w) || w.ValueKind != JsonValueKind.Object)
            return;

        double fraction = double.NaN;
        if (w.TryGetProperty("used_percent", out JsonElement used) && used.ValueKind == JsonValueKind.Number)
            fraction = Math.Clamp(used.GetDouble() / 100.0, 0, 1);

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("reset_at", out JsonElement reset) && reset.ValueKind == JsonValueKind.Number)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());

        if (!double.IsNaN(fraction))
            snaps.Add(new UsageSnapshot("codex", label, fraction, resetsAt, now, FetchStatus.Ok));
    }

    private static (string Token, string? AccountId) ReadCredentials()
    {
        // No token refresh here either — the codex CLI owns auth.json and refreshes it.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AuthPath));
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("OPENAI_API_KEY", out JsonElement key) &&
                key.GetString() is { Length: > 0 } apiKey)
                return (apiKey, null);

            if (root.TryGetProperty("tokens", out JsonElement tokens) &&
                tokens.TryGetProperty("access_token", out JsonElement at) &&
                at.GetString() is { Length: > 0 } token)
            {
                string? accountId = tokens.TryGetProperty("account_id", out JsonElement acc)
                    ? acc.GetString() : null;
                return (token, accountId);
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new UnauthorizedAccessException("Codex credentials unreadable.", e);
        }
        throw new UnauthorizedAccessException("No Codex token found.");
    }
}
