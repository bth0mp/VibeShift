using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VibeShift.Core.Games;

/// <summary>Folders games get installed into, from the launchers' own records.</summary>
public static partial class GameLibraries
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathEntry();

    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdf) =>
        SteamPathEntry().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static string? ParseEpicInstallLocation(string itemJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            return doc.RootElement.TryGetProperty("InstallLocation", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static IEnumerable<string> Steam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steam) return [];
        steam = steam.Replace('/', '\\');
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? ParseSteamLibraryFolders(File.ReadAllText(vdf)) : [steam];
        return libraries.Select(l => Path.Combine(l, "steamapps", "common"));
    }

    private static IEnumerable<string> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
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
