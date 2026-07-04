using System.Text;
using System.Text.Json;

namespace Tallybar;

internal static class ProviderUtil
{
    /// <summary>Extract a string claim from a JWT payload without validating the signature.</summary>
    public static string? JwtClaim(string jwt, string claim)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty(claim, out JsonElement e) ? e.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>Percent-used from a fraction remaining in [0,1].</summary>
    public static double UsedFromRemainingFraction(double remaining) => Math.Clamp(1 - remaining, 0, 1);

    public static DateTimeOffset? ParseReset(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), out var d))
            return d;
        if (e.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(e.GetInt64());
        return null;
    }
}
