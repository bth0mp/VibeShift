using System.Text.RegularExpressions;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Games;

public static class GameRules
{
    private static readonly Regex[] ExcludedFiles = Wildcards(
        "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "UnityCrashHandler*",
        "CrashReport*", "*CrashHandler*", "EasyAntiCheat*", "BEService*", "vcredist*", "DXSETUP", "*Setup*",
        "*Installer*", "wallpaper32", "wallpaper64");

    // ponytail: name list of non-game Steam apps (tools, engines, creative apps); upgrade path is reading the
    // app type from Steam's appinfo.vdf
    private static readonly Regex[] ExcludedFolders = Wildcards(
        "wallpaper_engine", "Steamworks Shared", "OBS Studio", "Soundpad", "Lossless Scaling", "Blender", "Godot*",
        "RPG Maker*", "GameMaker*", "Aseprite", "Krita", "Substance 3D*", "Pixel Composer", "Bforartists");

    private static Regex[] Wildcards(params string[] patterns) =>
        patterns.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase)).ToArray();

    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Agent",
        "EADesktop", "EABackgroundService", "EALocalHostSvc", "GalaxyClient", "GalaxyClient Helper", "RiotClientServices",
        "RiotClientUx", "upc", "UbisoftConnect", "XboxPcApp", "XboxPcAppFT", "gamingservices",
    };

    /// <summary>Game stores and their helpers: closing one can take the running game down with it.</summary>
    public static bool IsGameLauncher(string processName) => Launchers.Contains(processName);

    public static bool IsGame(string imagePath, IReadOnlyList<string> roots, IReadOnlyList<string> myGames, bool hasVisibleWindow) =>
        IsMyGame(imagePath, myGames) || (IsCandidate(imagePath, roots) && hasVisibleWindow);

    public static bool IsMyGame(string imagePath, IReadOnlyList<string> myGames) =>
        myGames.Any(g => string.Equals(g, imagePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Under a library root (on a folder boundary) and not a launcher, helper or non-game app.</summary>
    public static bool IsCandidate(string imagePath, IReadOnlyList<string> roots)
    {
        if (!roots.Any(r => imagePath.StartsWith(r.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase))) return false;
        var file = Path.GetFileNameWithoutExtension(imagePath);
        if (ExcludedFiles.Any(rx => rx.IsMatch(file))) return false;
        var folders = Path.GetDirectoryName(imagePath)?.Split('\\') ?? [];
        return !folders.Any(f => ExcludedFolders.Any(rx => rx.IsMatch(f)));
    }
}

/// <summary>Finds running games each poll. Image paths are cached per process instance; windows are only checked for candidates.</summary>
public sealed class GameWatcher(
    IReadOnlyList<string> roots,
    IReadOnlyList<string> myGames,
    Func<int, string?> imagePath,
    Func<int, bool> hasVisibleWindow,
    int? sessionId = null) // only this logon session's games count; null = any (tests)
{
    private readonly Dictionary<(int Pid, long CreateTime), string?> paths = [];

    /// <summary>Thread-safe: Auto mode's poll and a manual switch may both scan.</summary>
    public IReadOnlyCollection<int> Scan(IReadOnlyList<ProcInfo> snapshot)
    {
        lock (paths) return ScanLocked(snapshot);
    }

    private List<int> ScanLocked(IReadOnlyList<ProcInfo> snapshot)
    {
        var live = new HashSet<(int, long)>();
        var games = new List<int>();
        foreach (var p in snapshot)
        {
            if (sessionId is { } mine && p.SessionId != mine) continue;
            var key = (p.Pid, p.CreateTime);
            live.Add(key);
            if (!paths.TryGetValue(key, out var path)) paths[key] = path = imagePath(p.Pid);
            if (path is null) continue;
            if (GameRules.IsMyGame(path, myGames) || (GameRules.IsCandidate(path, roots) && hasVisibleWindow(p.Pid)))
                games.Add(p.Pid);
        }
        foreach (var gone in paths.Keys.Where(k => !live.Contains(k)).ToList()) paths.Remove(gone);
        return games;
    }
}
