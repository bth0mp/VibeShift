using System.Text.Json;
using System.Text.Json.Serialization;
using VibeShift.Core.Catalog;

namespace VibeShift.Core.State;

public enum ThemeChoice { System, Dark, Light }

public sealed class Settings
{
    /// <summary>0 = written by 1.0.0 (or new). 2 = 1.0.1+, where Microsoft 365 Copilot got its own row.</summary>
    public int SchemaVersion { get; set; }

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

    /// <summary>Switch back to vibe coding once the game closes, even when gaming was started by hand.</summary>
    public bool ReturnWhenGameCloses { get; set; } = true;

    /// <summary>Entry ids opened every time you switch back, whether or not they were closed for gaming.</summary>
    public List<string> VibeApps { get; set; } = [];

    /// <summary>How to start each app VibeShift has seen running (exe path or Store AUMID), so vibe apps can be opened.</summary>
    public Dictionary<string, ClosedApp> LaunchTargets { get; set; } = [];
    public List<string> BoostServices { get; set; } = ["SysMain", "WSearch"];

    public bool IsOn(CatalogEntry e) => Toggles.TryGetValue(e.Id, out var on) ? on : e.DefaultOn;

    /// <summary>Drops null list entries from a hand-edited file and upgrades older files' choices.</summary>
    internal void Normalize()
    {
        foreach (var list in new[] { KnownApps, CustomApps, MyGames, BoostServices, VibeApps }) list.RemoveAll(x => x is null);
        foreach (var bad in LaunchTargets.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) LaunchTargets.Remove(bad);
        // 1.0.0 covered the Microsoft 365 Copilot app under the Copilot toggle; keep the user's choice for it.
        if (SchemaVersion < 2 && Toggles.TryGetValue("copilot", out var on) && !Toggles.ContainsKey("m365copilot"))
            Toggles["m365copilot"] = on;
        SchemaVersion = 2;
    }

    /// <summary>An independent copy, so background work never reads a list the UI is editing.</summary>
    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
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

    /// <summary>Private RAM of the processes actually killed.</summary>
    public long KilledBytes { get; set; }

    /// <summary>
    /// What the FREED tile shows: the measured gain in free RAM, or what was killed if that's larger
    /// (a game loading during the measurement can eat the measured gain).
    /// </summary>
    [JsonIgnore]
    public long Freed => Math.Max(Math.Max(0, AvailAfter - AvailBefore), KilledBytes);

    /// <summary>Drops null list entries from a hand-edited file (they'd throw on every switch back).</summary>
    internal void Normalize()
    {
        Closed.RemoveAll(c => c is null);
        StoppedServices.RemoveAll(x => x is null);
        ClosedEntryIds.RemoveAll(x => x is null);
    }
}
