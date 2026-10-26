namespace VibeShift.Core.Catalog;

/// <summary>Processes VibeShift must never close or let the user add, whatever the catalog or settings say.</summary>
public static class BlockList
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "sihost", "fontdrvhost", "RuntimeBroker", "ctfmon", "audiodg", "MsMpEng", "SearchHost",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
    };

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + @"\";

    public static bool IsBlocked(string name, string? imagePath, int pid, int selfPid, IReadOnlySet<int> gamePids) =>
        pid == selfPid
        || gamePids.Contains(pid)
        || SystemNames.Contains(name)
        || (imagePath is not null && imagePath.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase));
}
