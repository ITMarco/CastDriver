using System.Net.Http;
using System.Text.Json;

namespace CastDriver.UI;

// Checks the latest GitHub release against the running version. Network-failure safe:
// any error just returns null (no banner), so it never blocks or breaks startup.
public static class UpdateChecker
{
    private static readonly string LatestApi =
        $"https://api.github.com/repos/{AppInfo.Repo}/releases/latest";

    public sealed record Result(
        bool Available, string LatestTag, string Url, string? Feature,
        string? AssetUrl, string? AssetSha256);

    public static async Task<Result?> CheckAsync(Version current)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CastDriver-update-check");

            var json = await http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var latest = ParseTag(tag);
            if (latest == null || url == null) return null;

            var available = Normalize(latest) > Normalize(current);
            var assetUrl  = FindAssetUrl(root, AppInfo.UpdateAssetName);
            var sha       = ExtractSha256(body, AppInfo.UpdateAssetName);
            return new Result(available, tag!, url, ExtractFeature(body), assetUrl, sha);
        }
        catch { return null; }
    }

    // The direct download URL for the release asset matching this build's variant.
    private static string? FindAssetUrl(JsonElement root, string assetName)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) continue;
            return a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        }
        return null;
    }

    // Pull the asset's SHA-256 from the release notes — a line like "CastDriver.exe: <64 hex>".
    private static string? ExtractSha256(string? body, string assetName)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        foreach (var raw in body.Split('\n'))
        {
            if (raw.IndexOf(assetName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(raw, "[0-9a-fA-F]{64}");
            if (m.Success) return m.Value;
        }
        return null;
    }

    // Pull the headline feature out of the release notes — the first line that reads
    // "Feature: <text>" (markdown bold/bullets tolerated, e.g. "**Feature:** …").
    private static string? ExtractFeature(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        const string key = "feature:";
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('*', '-', '#', '>', ' ', '\t');
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var text = line[key.Length..].Trim().Trim('*').Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.TrimStart('v', 'V');
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : null;
    }

    // Compare on major.minor.patch only, treating unspecified components as 0.
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
