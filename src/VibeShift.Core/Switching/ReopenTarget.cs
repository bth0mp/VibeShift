using System.Diagnostics;
using System.Text.RegularExpressions;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

public static partial class ReopenTarget
{
    [GeneratedRegex(@"^app-\d+(\.\d+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionedFolder();

    /// <summary>
    /// Squirrel-installed apps (e.g. Claude's non-Store installer) run from a versioned app-x.y.z folder that
    /// an update on quit can delete; the same-named stub one folder up always survives, so reopen through it.
    /// </summary>
    public static string PreferStub(string exePath, Func<string, bool> exists)
    {
        var dir = Path.GetDirectoryName(exePath);
        var parent = dir is null ? null : Path.GetDirectoryName(dir);
        if (dir is null || parent is null || !VersionedFolder().IsMatch(Path.GetFileName(dir))) return exePath;
        var stub = Path.Combine(parent, Path.GetFileName(exePath));
        return exists(stub) ? stub : exePath;
    }

    /// <summary>
    /// Packaged (Store/MSIX) apps can't be started from their WindowsApps exe path, so they go through the
    /// shell by AppUserModelID. Everything else starts from its recorded exe; if VibeShift itself runs
    /// elevated, that also goes through the shell so the app doesn't inherit admin rights.
    /// </summary>
    public static ProcessStartInfo ToStartInfo(ClosedApp app, bool elevated = false)
    {
        if (app.Aumid is { } aumid) return new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid);
        if (app.ExePath is not { } exe) throw new ArgumentException($"{app.Name} has nothing to reopen it with.", nameof(app));
        if (elevated) return new ProcessStartInfo("explorer.exe", $"\"{exe}\"");
        return new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
    }
}
