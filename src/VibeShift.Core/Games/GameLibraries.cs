using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VibeShift.Core.Games;

/// <summary>An installed game the Play button can start. Id is "steam:&lt;appid&gt;", "epic:&lt;AppName&gt;" or "exe:&lt;path&gt;".</summary>
public sealed record InstalledGame(string Id, string Name, string LaunchUri, DateTime LastPlayedUtc);

/// <summary>Folders games get installed into, from the launchers' own records.</summary>
public static partial class GameLibraries
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathEntry();

    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdf) =>
        SteamPathEntry().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    /// <summary>The install folder of an Epic game, or null for engines and tools (AppCategories without "games").</summary>
    public static string? ParseEpicInstallLocation(string itemJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                && !cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
                return null;
            return root.TryGetProperty("InstallLocation", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"")]
    private static partial Regex VdfPair();

    /// <summary>A fully installed Steam game from its appmanifest_*.acf, or null for tools and partial installs.</summary>
    public static InstalledGame? ParseSteamAppManifest(string acf)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPair().Matches(acf)) values.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value); // top level wins
        if (!values.TryGetValue("appid", out var appId) || !values.TryGetValue("name", out var name)) return null;
        if (!int.TryParse(values.GetValueOrDefault("StateFlags"), out var flags) || (flags & 4) == 0) return null; // 4 = fully installed
        if (GameRules.IsNonGameFolder(values.GetValueOrDefault("installdir") ?? "") || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase))
            return null;
        var lastPlayed = long.TryParse(values.GetValueOrDefault("LastPlayed"), out var secs) && secs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
            : DateTime.MinValue;
        return new InstalledGame("steam:" + appId, name, "steam://rungameid/" + appId, lastPlayed);
    }

    /// <summary>An installed Epic game from its .item manifest, launched through the Epic launcher; null for engines and tools.</summary>
    public static InstalledGame? ParseEpicGame(string itemJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;
            string? Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                && !cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (Get("AppName") is not { } app) return null;
            string id = Get("CatalogNamespace") is { } ns && Get("CatalogItemId") is { } item ? $"{ns}%3A{item}%3A{app}" : app;
            return new InstalledGame("epic:" + app, Get("DisplayName") ?? app,
                $"com.epicgames.launcher://apps/{id}?action=launch&silent=true", DateTime.MinValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Games the Play button can start: Steam, Epic and the user's own list, most recently played first.</summary>
    public static IReadOnlyList<InstalledGame> InstalledGames(IReadOnlyList<string> myGames)
    {
        var games = new List<InstalledGame>();
        Try(() =>
        {
            foreach (var lib in SteamLibraries())
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                    if (ParseSteamAppManifest(File.ReadAllText(acf)) is { } g) games.Add(g);
            }
        });
        Try(() =>
        {
            var dir = EpicManifestDir();
            if (!Directory.Exists(dir)) return;
            foreach (var item in Directory.GetFiles(dir, "*.item"))
                if (ParseEpicGame(File.ReadAllText(item)) is { } g) games.Add(g);
        });
        foreach (var exe in myGames)
            games.Add(new InstalledGame("exe:" + exe.ToLowerInvariant(), Catalog.AppCatalog.Custom(exe).Name, exe, DateTime.MinValue));
        return games.DistinctBy(g => g.Id).OrderByDescending(g => g.LastPlayedUtc).ThenBy(g => g.Name).ToList();
    }

    /// <summary>Every existing library root on this PC. Each source fails independently.</summary>
    public static IReadOnlyList<string> Discover()
    {
        var roots = new List<string>();
        Try(() => roots.AddRange(Steam()));
        Try(() => roots.AddRange(Epic()));
        Try(() => roots.AddRange(Gog()));
        Try(() =>
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                roots.Add(Path.Combine(d.Name, "Program Files", "EA Games"));
                roots.Add(Path.Combine(d.Name, "EA Games"));
                roots.Add(Path.Combine(d.Name, "XboxGames"));
            }
        });
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Steam() => SteamLibraries().Select(l => Path.Combine(l, "steamapps", "common"));

    private static IReadOnlyList<string> SteamLibraries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steam) return [];
        steam = steam.Replace('/', '\\');
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        return File.Exists(vdf) ? ParseSteamLibraryFolders(File.ReadAllText(vdf)) : [steam];
    }

    private static string EpicManifestDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    private static IEnumerable<string> Epic()
    {
        var dir = EpicManifestDir();
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.item")
            .Select(f => ParseEpicInstallLocation(File.ReadAllText(f)))
            .OfType<string>()
            .ToList();
    }

    private static IEnumerable<string> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) return [];
        var paths = new List<string>();
        foreach (var name in games.GetSubKeyNames())
        {
            using var game = games.OpenSubKey(name);
            if (game?.GetValue("path") is string p) paths.Add(p);
        }
        return paths;
    }

    private static void Try(Action source)
    {
        try { source(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            State.Log.Write("Game library source skipped: " + e.Message);
        }
    }
}
