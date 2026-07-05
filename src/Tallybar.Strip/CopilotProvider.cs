using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tallybar;

/// <summary>
/// GitHub Copilot usage via the GitHub CLI's local OAuth token (`gh auth token`).
/// No token of our own is stored. Configured only when `gh` is installed and signed in.
/// </summary>
public sealed class CopilotProvider : IProvider
{
    private const string UsageUrl = "https://api.github.com/copilot_internal/user";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Id => "copilot";
    public string DisplayName => "Copilot";
    public bool IsConfigured => GhToken() is not null;

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        string token = GhToken() ?? throw new UnauthorizedAccessException("GitHub CLI not signed in.");

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("Editor-Version", "vscode/1.96.2");
        req.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.26.7");
        req.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.26.7");
        req.Headers.Add("X-Github-Api-Version", "2025-04-01");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Copilot token rejected.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        DateTimeOffset? reset = null;
        if (root.TryGetProperty("quota_reset_date", out JsonElement rd) &&
            DateTimeOffset.TryParse(rd.GetString(), out var d))
            reset = d;

        var snaps = new List<UsageSnapshot>();
        if (root.TryGetProperty("quota_snapshots", out JsonElement quotas) &&
            quotas.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty q in quotas.EnumerateObject())
            {
                JsonElement v = q.Value;
                if (v.TryGetProperty("unlimited", out JsonElement unl) && unl.ValueKind == JsonValueKind.True)
                    continue;
                double used = double.NaN;
                if (v.TryGetProperty("percent_remaining", out JsonElement pr) && pr.ValueKind == JsonValueKind.Number)
                    used = Math.Clamp((100 - pr.GetDouble()) / 100.0, 0, 1);
                else if (v.TryGetProperty("remaining", out JsonElement rem) && rem.ValueKind == JsonValueKind.Number &&
                         v.TryGetProperty("entitlement", out JsonElement ent) && ent.ValueKind == JsonValueKind.Number &&
                         ent.GetDouble() > 0)
                    used = Math.Clamp(1 - rem.GetDouble() / ent.GetDouble(), 0, 1);

                if (!double.IsNaN(used))
                    snaps.Add(new UsageSnapshot("copilot", Prettify(q.Name), used, reset, now, FetchStatus.Ok));
            }
        }
        if (snaps.Count == 0)
            throw new InvalidDataException("No Copilot quota data.");
        return snaps;
    }

    private static string Prettify(string key) => key switch
    {
        "premium_interactions" => "premium",
        "chat" => "chat",
        "completions" => "completions",
        _ => key.Replace('_', ' '),
    };

    private static string? _cachedToken;
    private static bool _probed;

    private static string? GhToken()
    {
        if (_probed) return _cachedToken;
        _probed = true;
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(4000);
            _cachedToken = outp.Length > 0 ? outp : null;
        }
        catch { _cachedToken = null; }
        return _cachedToken;
    }
}
