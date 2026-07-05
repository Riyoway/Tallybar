using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tallybar;

/// <summary>
/// Gemini (Google gemini-cli) usage via the CLI's local OAuth credentials
/// (~/.gemini/oauth_creds.json). Refreshes an expired token in place using the
/// client id/secret shipped with the installed gemini-cli (or env/config overrides).
/// </summary>
public sealed partial class GeminiProvider : IProvider
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string QuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string CredsPath => Path.Combine(Home, ".gemini", "oauth_creds.json");

    public string Id => "gemini";
    public string DisplayName => "Gemini";
    public bool IsConfigured => File.Exists(CredsPath);

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        string token = await AccessTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, QuotaUrl)
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Gemini token rejected — run `gemini` to sign in.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Group buckets by model, keep the lowest remaining fraction (most-used) per model.
        var byModel = new Dictionary<string, (double used, DateTimeOffset? reset)>();
        if (doc.RootElement.TryGetProperty("buckets", out JsonElement buckets) &&
            buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement b in buckets.EnumerateArray())
            {
                if (!b.TryGetProperty("remainingFraction", out JsonElement rf) || rf.ValueKind != JsonValueKind.Number)
                    continue;
                string model = b.TryGetProperty("modelId", out JsonElement m) ? m.GetString() ?? "gemini" : "gemini";
                double used = ProviderUtil.UsedFromRemainingFraction(rf.GetDouble());
                DateTimeOffset? reset = b.TryGetProperty("resetTime", out JsonElement rt) ? ProviderUtil.ParseReset(rt) : null;
                if (!byModel.TryGetValue(model, out var cur) || used > cur.used)
                    byModel[model] = (used, reset);
            }
        }
        if (byModel.Count == 0)
            throw new InvalidDataException("No Gemini quota buckets.");

        return [.. byModel.OrderByDescending(kv => kv.Value.used)
            .Select(kv => new UsageSnapshot("gemini", ShortModel(kv.Key), kv.Value.used, kv.Value.reset, now, FetchStatus.Ok))];
    }

    private static string ShortModel(string modelId)
    {
        string m = modelId.ToLowerInvariant();
        if (m.Contains("flash-lite")) return "flash-lite";
        if (m.Contains("flash")) return "flash";
        if (m.Contains("pro")) return "pro";
        return modelId;
    }

    // --- token handling ---

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredsPath)))
        {
            JsonElement root = doc.RootElement;
            string? access = root.TryGetProperty("access_token", out JsonElement a) ? a.GetString() : null;
            long expMs = root.TryGetProperty("expiry_date", out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64() : 0;
            bool expired = expMs != 0 && DateTimeOffset.FromUnixTimeMilliseconds(expMs) <= DateTimeOffset.UtcNow;

            if (!expired && !string.IsNullOrEmpty(access))
                return access;

            string? refresh = root.TryGetProperty("refresh_token", out JsonElement r) ? r.GetString() : null;
            if (string.IsNullOrEmpty(refresh))
                throw new UnauthorizedAccessException("Gemini token expired and no refresh token.");
            return await RefreshAsync(refresh, ct);
        }
    }

    private async Task<string> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        (string clientId, string clientSecret) = ClientCredentials()
            ?? throw new UnauthorizedAccessException("Gemini OAuth client id/secret not found.");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });
        using HttpResponseMessage res = await Http.PostAsync(TokenUrl, content, ct);
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        string access = doc.RootElement.GetProperty("access_token").GetString()!;
        long expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement ei) ? ei.GetInt64() : 3600;

        WriteBackToken(access, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds());
        return access;
    }

    private static void WriteBackToken(string access, long expiryMs)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredsPath));
            var map = new Dictionary<string, JsonElement>();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.Clone();
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var kv in map)
                {
                    if (kv.Key is "access_token" or "expiry_date") continue;
                    w.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(w);
                }
                w.WriteString("access_token", access);
                w.WriteNumber("expiry_date", expiryMs);
                w.WriteEndObject();
            }
            File.WriteAllBytes(CredsPath, ms.ToArray());
        }
        catch { /* best effort; we still return the fresh token in memory */ }
    }

    private static (string, string)? ClientCredentials()
    {
        string? envId = Environment.GetEnvironmentVariable("GEMINI_CLIENT_ID");
        string? envSecret = Environment.GetEnvironmentVariable("GEMINI_CLIENT_SECRET");
        if (!string.IsNullOrEmpty(envId) && !string.IsNullOrEmpty(envSecret)) return (envId, envSecret);

        string configPath = Path.Combine(Home, ".gemini", "client_config.json");
        if (File.Exists(configPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
                string? id = doc.RootElement.TryGetProperty("client_id", out JsonElement i) ? i.GetString() : null;
                string? sec = doc.RootElement.TryGetProperty("client_secret", out JsonElement s) ? s.GetString() : null;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sec)) return (id, sec);
            }
            catch { }
        }
        // Otherwise read the client id/secret straight from the installed gemini-cli.
        // We never ship these ourselves — they belong to the user's CLI install.
        return ScrapeCliCredentials();
    }

    // gemini-cli embeds its OAuth client id/secret in oauth2.js; read them from the install.
    private static (string, string)? ScrapeCliCredentials()
    {
        foreach (string js in OAuthJsCandidates())
        {
            try
            {
                string text = File.ReadAllText(js);
                Match id = ClientIdRegex().Match(text);
                Match secret = ClientSecretRegex().Match(text);
                if (id.Success && secret.Success)
                    return (id.Groups[1].Value, secret.Groups[1].Value);
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> OAuthJsCandidates()
    {
        foreach (string root in NodeModuleRoots())
        {
            // Classic layout: a separate gemini-cli-core package.
            string oauth = Path.Combine(root, @"@google\gemini-cli-core\dist\src\code_assist\oauth2.js");
            if (File.Exists(oauth)) yield return oauth;

            // Core nested inside the CLI package.
            string nested = Path.Combine(root, @"@google\gemini-cli\node_modules\@google\gemini-cli-core\dist\src\code_assist\oauth2.js");
            if (File.Exists(nested)) yield return nested;

            // Bundled (esbuild) layout: the constants live in bundle/chunk-*.js.
            string bundle = Path.Combine(root, @"@google\gemini-cli\bundle");
            if (Directory.Exists(bundle))
                foreach (string js in Directory.EnumerateFiles(bundle, "*.js"))
                    yield return js;
        }
    }

    private static IEnumerable<string> NodeModuleRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(appData, "npm", "node_modules");
        yield return Path.Combine(Home, ".bun", "install", "global", "node_modules");

        // Whatever `npm root -g` reports (covers non-default prefixes, nvm, volta, …).
        string? npmRoot = RunCapture("cmd.exe", "/c npm root -g");
        if (!string.IsNullOrWhiteSpace(npmRoot) && Directory.Exists(npmRoot.Trim()))
            yield return npmRoot.Trim();

        string fnmRoot = Path.Combine(localApp, "fnm", "node-versions");
        if (Directory.Exists(fnmRoot))
            foreach (string ver in Directory.EnumerateDirectories(fnmRoot))
                yield return Path.Combine(ver, "installation", "lib", "node_modules");
    }

    private static string? RunCapture(string file, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000); // node/npm cold start can exceed a few seconds
            return outp;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"OAUTH_CLIENT_ID\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex ClientIdRegex();

    [GeneratedRegex(@"OAUTH_CLIENT_SECRET\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex ClientSecretRegex();
}
