using System.Text.RegularExpressions;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Games;

public static class GameRules
{
    private static readonly Regex[] ExcludedFiles =
        new[]
        {
            "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EpicWebHelper", "UnityCrashHandler*",
            "CrashReport*", "*CrashHandler*", "EasyAntiCheat*", "BEService*", "vcredist*", "DXSETUP", "*Setup*",
            "*Installer*", "wallpaper32", "wallpaper64",
        }
        .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase))
        .ToArray();

    // ponytail: name list of non-game Steam apps; upgrade path is reading the app type from Steam's appinfo.vdf
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "wallpaper_engine", "Steamworks Shared", "OBS Studio", "Soundpad", "Lossless Scaling" };

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
        return !folders.Any(ExcludedFolders.Contains);
    }
}

/// <summary>Finds running games each poll. Image paths are cached per process instance; windows are only checked for candidates.</summary>
public sealed class GameWatcher(
    IReadOnlyList<string> roots,
    IReadOnlyList<string> myGames,
    Func<int, string?> imagePath,
    Func<int, bool> hasVisibleWindow)
{
    private readonly Dictionary<(int Pid, long CreateTime), string?> paths = [];

    public IReadOnlyCollection<int> Scan(IReadOnlyList<ProcInfo> snapshot)
    {
        var live = new HashSet<(int, long)>();
        var games = new List<int>();
        foreach (var p in snapshot)
        {
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
