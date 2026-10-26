using VibeShift.Core.Catalog;

namespace VibeShift.Core.State;

public enum ThemeChoice { System, Dark, Light }

public sealed class Settings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>Entry id -> on/off. Missing ids use the entry's default.</summary>
    public Dictionary<string, bool> Toggles { get; set; } = [];

    /// <summary>Entry ids seen running at least once, so their rows stay listed.</summary>
    public List<string> KnownApps { get; set; } = [];

    public List<string> CustomApps { get; set; } = [];
    public List<string> MyGames { get; set; } = [];
    public bool AutoMode { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ExtraBoost { get; set; }
    public int ForceCloseSeconds { get; set; } = 5;
    public List<string> BoostServices { get; set; } = ["SysMain", "WSearch"];

    public bool IsOn(CatalogEntry e) => Toggles.TryGetValue(e.Id, out var on) ? on : e.DefaultOn;
}

public sealed record ClosedApp(string EntryId, string Name, string? ExePath, string? Aumid);

/// <summary>Exists on disk only while in gaming mode, so a crash or reboot can still switch back.</summary>
public sealed class SessionState
{
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public bool Automatic { get; set; }
    public long AvailBefore { get; set; }
    public long AvailAfter { get; set; }

    /// <summary>Apps to reopen, in the order they were closed.</summary>
    public List<ClosedApp> Closed { get; set; } = [];

    public List<string> StoppedServices { get; set; } = [];
    public bool WslShutDown { get; set; }

    /// <summary>Every entry closed, including MCP and CLI rows that never reopen; drives row tags.</summary>
    public List<string> ClosedEntryIds { get; set; } = [];
}
