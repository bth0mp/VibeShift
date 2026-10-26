namespace VibeShift.Core.Games;

public enum Mode { Coding, Gaming }

public enum AutoAction { None, StartGaming, BackToCoding }

/// <summary>
/// Decides when Auto mode switches. The user's clicks win: switching back by hand while a game
/// runs suppresses that game, and starting gaming by hand means Auto mode never switches back.
/// </summary>
public sealed class AutoModeTracker(TimeSpan backDelay)
{
    private readonly HashSet<int> suppressed = [];
    private bool autoOwned;
    private DateTime lastGameSeen;

    public AutoAction Update(IReadOnlyCollection<int> gamePids, Mode mode, DateTime nowUtc)
    {
        suppressed.IntersectWith(gamePids);

        if (mode == Mode.Coding)
        {
            if (!gamePids.Any(pid => !suppressed.Contains(pid))) return AutoAction.None;
            autoOwned = true;
            lastGameSeen = nowUtc;
            return AutoAction.StartGaming;
        }

        if (!autoOwned) return AutoAction.None;
        if (gamePids.Count > 0)
        {
            lastGameSeen = nowUtc;
            return AutoAction.None;
        }
        if (nowUtc - lastGameSeen <= backDelay) return AutoAction.None;
        autoOwned = false;
        return AutoAction.BackToCoding;
    }

    public void ManualSwitch(Mode newMode, IReadOnlyCollection<int> gamePidsNow)
    {
        autoOwned = false;
        if (newMode == Mode.Coding) suppressed.UnionWith(gamePidsNow);
    }
}
