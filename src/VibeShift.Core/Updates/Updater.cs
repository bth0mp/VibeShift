using System.Security.Cryptography;
using System.Text.Json;

namespace VibeShift.Core.Updates;

public sealed record Release(Version Version, string ExeUrl, string Sha256, string PageUrl);

/// <summary>Finds the latest GitHub release and swaps the running exe for it. Only runs when the user asks.</summary>
public static class Updater
{
    public const string LatestUrl = "https://api.github.com/repos/bth0mp/VibeShift/releases/latest";

    /// <summary>The only place an update is ever downloaded from: this repo's own releases.</summary>
    private const string DownloadPrefix = "https://github.com/bth0mp/VibeShift/releases/download/";

    public static async Task<Release?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.UserAgent.ParseAdd("VibeShift");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The release's version and VibeShift.exe, or null without an exe and a SHA-256 to check it against.</summary>
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var version)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "VibeShift.exe") continue;
            var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            return new Release(version, url, digest[7..], root.GetProperty("html_url").GetString()!);
        }
        return null;
    }

    /// <summary>Compares major.minor.patch (the assembly's version has a 4th part releases don't).</summary>
    public static bool IsNewer(Release r, Version current) => Plain(r.Version) > Plain(current);

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Downloads the new exe next to the running one, checks its SHA-256 and swaps it in. The old exe stays as
    /// .old until the next start (a running exe can't be deleted). A bad download changes nothing.
    /// </summary>
    public static async Task InstallAsync(HttpClient http, Release r, string exePath, CancellationToken ct = default)
    {
        string fresh = exePath + ".new";
        try
        {
            using (var resp = await http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var file = File.Create(fresh);
                await resp.Content.CopyToAsync(file, ct);
            }
            string hash;
            await using (var file = File.OpenRead(fresh)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download didn't match its SHA-256, so nothing was changed.");
            Swap(exePath, fresh, exePath + ".old");
        }
        finally
        {
            File.Delete(fresh);
        }
    }

    /// <summary>A running exe can be renamed but not overwritten: move it aside, move the new one in, undo if that fails.</summary>
    public static void Swap(string exePath, string fresh, string old)
    {
        File.Move(exePath, old, overwrite: true);
        try
        {
            File.Move(fresh, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Deletes the exe an update replaced, now that it's no longer running.</summary>
    public static void CleanUp(string exePath)
    {
        try
        {
            File.Delete(exePath + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // still running (another copy) or not ours to delete; try again next start
        }
    }
}
