using System.Diagnostics;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

public static class ReopenTarget
{
    /// <summary>
    /// Packaged (Store/MSIX) apps can't be started from their WindowsApps exe path, so they go
    /// through the shell by AppUserModelID. Everything else starts from its recorded exe.
    /// </summary>
    public static ProcessStartInfo ToStartInfo(ClosedApp app)
    {
        if (app.Aumid is { } aumid) return new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid);
        if (app.ExePath is not { } exe) throw new ArgumentException($"{app.Name} has nothing to reopen it with.", nameof(app));
        return new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
    }
}
