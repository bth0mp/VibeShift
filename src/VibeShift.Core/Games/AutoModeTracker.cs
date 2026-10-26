namespace VibeShift.Core.Games;

public enum Mode { Coding, Gaming }

public enum AutoAction { None, StartGaming, BackToCoding }

/// <summary>
/// Decides when VibeShift switches by itself. Auto mode starts gaming when a game starts. Switching back
/// happens once the games seen during the session have all closed, if Auto mode started it or the user
/// turned on "Switch back when my game closes". Clicks still win: switching back by hand while a game runs
/// suppresses that game until a new one starts.
/// </summary>
public sealed class AutoModeTracker(TimeSpan backDelay)
{
    private readonly HashSet<int> suppressed = [];
    private bool returnArmed; // switch back once this session's games have closed
    private bool sawGame;
    private DateTime lastGameSeen;

    /// <param name="autoStart">Auto mode is on: a new game may start gaming mode.</param>
    public AutoAction Update(IReadOnlyCollection<int> gamePids, Mode mode, DateTime nowUtc, bool autoStart = true)
    {
        suppressed.IntersectWith(gamePids);

        if (mode == Mode.Coding)
        {
            if (!autoStart || !gamePids.Any(pid => !suppressed.Contains(pid))) return AutoAction.None;
            Arm(nowUtc);
            return AutoAction.StartGaming;
        }

        if (!returnArmed) return AutoAction.None;
        if (gamePids.Count > 0)
        {
            sawGame = true;
            lastGameSeen = nowUtc;
            return AutoAction.None;
        }
        // Never saw a game this session: nothing to wait for, so leave it to the user.
        if (!sawGame || nowUtc - lastGameSeen <= backDelay) return AutoAction.None;
        returnArmed = false;
        return AutoAction.BackToCoding;
    }

    /// <summary>After a crash or reboot mid auto-session, keep the right to switch back by itself.</summary>
    public void ResumeAutoSession(DateTime nowUtc) => Arm(nowUtc);

    public void ManualSwitch(Mode newMode, IReadOnlyCollection<int> gamePidsNow, bool returnWhenGameCloses = false)
    {
        sawGame = false;
        returnArmed = newMode == Mode.Gaming && returnWhenGameCloses;
        if (newMode == Mode.Coding) suppressed.UnionWith(gamePidsNow);
    }

    private void Arm(DateTime nowUtc)
    {
        returnArmed = true;
        sawGame = true;
        lastGameSeen = nowUtc;
    }
}
