namespace VibeShift.Core.Games;

public enum Mode { Coding, Gaming }

public enum AutoAction { None, StartSoon, StartGaming, BackToCoding, Ask }

/// <summary>
/// Decides when VibeShift switches by itself. Auto mode starts gaming when a game starts. Switching back
/// happens once the games seen during the session have all closed, if Auto mode started it or the user
/// turned on "Switch back when my game closes". Clicks still win: switching back by hand while a game runs
/// suppresses that game until a new one starts.
/// </summary>
/// <param name="startDelay">
/// Auto mode's countdown: a new game first gives StartSoon (the App says "Gaming mode in 10 seconds", click to
/// cancel), and StartGaming once it's still running this long after. Zero switches at once.
/// </param>
public sealed class AutoModeTracker(TimeSpan backDelay, TimeSpan startDelay = default)
{
    private readonly HashSet<int> suppressed = [];
    private bool returnArmed; // switch back once this session's games have closed
    private bool sawGame;
    private DateTime lastGameSeen;
    private DateTime? countdownSince; // a new game was seen: Auto mode is counting down
    private bool asked; // "Ask me" has asked about the game(s) running now. Kept apart from the countdown: a game asked
                        // about and ignored, then Auto mode turned on, still gets the countdown (2026-11-23 review)

    /// <param name="autoStart">Auto mode (or "Ask me") is on: a new game may start gaming mode, or ask to.</param>
    /// <param name="canSwitch">
    /// False while VibeShift is busy (the Vibe page opening an app): games are still counted, but no switch is due
    /// yet, so the switch back isn't used up by a switch that couldn't run.
    /// </param>
    /// <param name="alive">
    /// Every running process. A game the user said to leave alone stays left alone while its process lives, not
    /// just while it's detected: between a splash and the main window a game has no window, so it drops out of
    /// <paramref name="gamePids"/> for a tick. Null (tests) falls back to the detected games.
    /// </param>
    /// <param name="askOnly">"Ask me": a new game gives Ask once, and nothing switches until the user does.</param>
    public AutoAction Update(IReadOnlyCollection<int> gamePids, Mode mode, DateTime nowUtc, bool autoStart = true, bool canSwitch = true,
        IReadOnlyCollection<int>? alive = null, bool askOnly = false)
    {
        suppressed.IntersectWith(alive ?? gamePids);

        if (mode == Mode.Coding)
        {
            if (!autoStart || !gamePids.Any(pid => !suppressed.Contains(pid)))
            {
                countdownSince = null; // gone before the countdown ended (a splash, a launcher): nothing starts
                asked = false;
                return AutoAction.None;
            }
            if (askOnly)
            {
                countdownSince = null;
                if (asked) return AutoAction.None;
                asked = true;
                return AutoAction.Ask;
            }
            bool fresh = countdownSince is null;
            countdownSince ??= nowUtc;
            if (canSwitch && nowUtc - countdownSince >= startDelay)
            {
                countdownSince = null;
                Arm(nowUtc);
                return AutoAction.StartGaming;
            }
            return fresh && startDelay > TimeSpan.Zero ? AutoAction.StartSoon : AutoAction.None;
        }

        // Whatever "Ask me" asked about belongs to before this gaming session. Left set, a game already running at the
        // first poll back in vibe coding mode (started while the switch back ran) was never asked about (2026-11-25 review).
        asked = false;
        if (!returnArmed) return AutoAction.None;
        if (gamePids.Count > 0)
        {
            sawGame = true;
            lastGameSeen = nowUtc;
            return AutoAction.None;
        }
        // Never saw a game this session: nothing to wait for, so leave it to the user.
        if (!canSwitch || !sawGame || nowUtc - lastGameSeen <= backDelay) return AutoAction.None;
        returnArmed = false;
        return AutoAction.BackToCoding;
    }

    /// <summary>
    /// Picks up a gaming session found on startup (after a crash, reboot or update). One Auto mode started had
    /// a game, so it switches back once things are quiet. One started by hand only switches back after a game
    /// is seen and closes (if the setting is on): with nothing running it might be about to start, or be a
    /// game VibeShift can't recognise.
    /// </summary>
    /// <param name="signedOutSince">
    /// The session is from before the last sign-out or shutdown, which closed the game whatever it was. With
    /// "switch back when my game closes" on, that counts as the game closing.
    /// </param>
    public void Resume(bool automatic, bool returnWhenGameCloses, DateTime nowUtc, bool signedOutSince = false)
    {
        if (automatic || (signedOutSince && returnWhenGameCloses)) Arm(nowUtc);
        else ManualSwitch(Mode.Gaming, [], returnWhenGameCloses);
    }

    /// <summary>"Keep my apps open" during the countdown: those games are left alone until a new one starts, as after a manual switch back.</summary>
    public void CancelStart(IReadOnlyCollection<int> gamePidsNow)
    {
        countdownSince = null;
        suppressed.UnionWith(gamePidsNow);
    }

    public void ManualSwitch(Mode newMode, IReadOnlyCollection<int> gamePidsNow, bool returnWhenGameCloses = false)
    {
        countdownSince = null;
        sawGame = false;
        returnArmed = newMode == Mode.Gaming && returnWhenGameCloses;
        if (newMode == Mode.Coding) suppressed.UnionWith(gamePidsNow);
    }

    /// <summary>
    /// "Switch back when my game closes" was changed in the middle of a session started by hand: it applies from now
    /// on, not from the next switch. Turned on, the switch back waits for a game seen from here on to close.
    /// </summary>
    public void SetReturnWhenGameCloses(bool on) => returnArmed = on;

    private void Arm(DateTime nowUtc)
    {
        returnArmed = true;
        sawGame = true;
        lastGameSeen = nowUtc;
    }
}
