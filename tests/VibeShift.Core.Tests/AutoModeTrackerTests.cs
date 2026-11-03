using VibeShift.Core.Games;

namespace VibeShift.Core.Tests;

public class AutoModeTrackerTests
{
    private static readonly int[] None = [];
    private readonly DateTime t0 = DateTime.UtcNow;
    private readonly AutoModeTracker T = new(TimeSpan.FromSeconds(10));

    [Fact]
    public void Game_start_switches_to_gaming() => Assert.Equal(AutoAction.StartGaming, T.Update(new[] { 7 }, Mode.Coding, t0));

    [Fact]
    public void Switches_back_only_after_ten_quiet_seconds()
    {
        T.Update(new[] { 7 }, Mode.Coding, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(1)));
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(10)));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddSeconds(11)));
    }

    private readonly AutoModeTracker C = new(TimeSpan.FromSeconds(10), startDelay: TimeSpan.FromSeconds(10));

    [Fact] // Auto mode says "Gaming mode in 10 seconds" first, so a click can keep the apps open
    public void Auto_start_counts_down_then_switches()
    {
        Assert.Equal(AutoAction.StartSoon, C.Update(new[] { 7 }, Mode.Coding, t0));
        Assert.Equal(AutoAction.None, C.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(3)));
        Assert.Equal(AutoAction.StartGaming, C.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(10)));
    }

    [Fact]
    public void Cancelled_countdown_leaves_that_game_alone_until_a_new_one()
    {
        C.Update(new[] { 7 }, Mode.Coding, t0);
        C.CancelStart(new[] { 7 });
        Assert.Equal(AutoAction.None, C.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(20)));
        Assert.Equal(AutoAction.StartSoon, C.Update(new[] { 7, 9 }, Mode.Coding, t0.AddSeconds(23)));
    }

    [Fact] // a splash or launcher that closes before the countdown ends starts nothing; the real game counts afresh
    public void Countdown_starts_over_when_the_game_is_gone_before_it_ends()
    {
        C.Update(new[] { 7 }, Mode.Coding, t0);
        Assert.Equal(AutoAction.None, C.Update(None, Mode.Coding, t0.AddSeconds(4)));
        Assert.Equal(AutoAction.StartSoon, C.Update(new[] { 8 }, Mode.Coding, t0.AddSeconds(6)));
        Assert.Equal(AutoAction.None, C.Update(new[] { 8 }, Mode.Coding, t0.AddSeconds(12)));
        Assert.Equal(AutoAction.StartGaming, C.Update(new[] { 8 }, Mode.Coding, t0.AddSeconds(16)));
    }

    [Fact]
    public void Countdown_that_ran_out_while_busy_switches_once_it_can()
    {
        C.Update(new[] { 7 }, Mode.Coding, t0);
        Assert.Equal(AutoAction.None, C.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(12), canSwitch: false));
        Assert.Equal(AutoAction.StartGaming, C.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(15)));
    }

    [Fact] // audit A1: the game closed while the Vibe page was opening an app; the switch back must wait, not vanish
    public void Switch_back_waits_while_VibeShift_is_busy()
    {
        T.Update(new[] { 7 }, Mode.Coding, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(11), canSwitch: false));
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(15), canSwitch: false));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddSeconds(18)));
        Assert.Equal(AutoAction.None, T.Update(new[] { 9 }, Mode.Coding, t0.AddSeconds(30), canSwitch: false)); // start waits too
        Assert.Equal(AutoAction.StartGaming, T.Update(new[] { 9 }, Mode.Coding, t0.AddSeconds(33)));
    }

    [Fact]
    public void Game_relaunch_within_delay_keeps_gaming()
    {
        T.Update(new[] { 7 }, Mode.Coding, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(1)));
        Assert.Equal(AutoAction.None, T.Update(new[] { 8 }, Mode.Gaming, t0.AddSeconds(5)));
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(6)));
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(15)));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddSeconds(16)));
    }

    [Fact]
    public void Manual_back_suppresses_running_game_until_a_new_one()
    {
        T.Update(new[] { 7 }, Mode.Coding, t0);
        T.ManualSwitch(Mode.Coding, new[] { 7 });
        Assert.Equal(AutoAction.None, T.Update(new[] { 7 }, Mode.Coding, t0.AddSeconds(3)));
        Assert.Equal(AutoAction.StartGaming, T.Update(new[] { 7, 9 }, Mode.Coding, t0.AddSeconds(6)));
    }

    [Fact] // 1.4: a session started by hand, resumed after a restart, waits to see a game before switching back
    public void Resumed_manual_session_waits_for_a_game_before_switching_back()
    {
        T.Resume(automatic: false, returnWhenGameCloses: true, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddMinutes(5)));   // no game yet: stay in gaming mode
        Assert.Equal(AutoAction.None, T.Update(new[] { 7 }, Mode.Gaming, t0.AddMinutes(6)));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddMinutes(6).AddSeconds(11)));
    }

    [Fact] // 1.4: with "switch back when my game closes" off, a resumed manual session never switches back by itself
    public void Resumed_manual_session_without_return_setting_stays() =>
        Assert.Equal(AutoAction.None, ResumedThenQuiet(automatic: false, returnWhenGameCloses: false));

    private AutoAction ResumedThenQuiet(bool automatic, bool returnWhenGameCloses)
    {
        T.Resume(automatic, returnWhenGameCloses, t0);
        T.Update(new[] { 7 }, Mode.Gaming, t0.AddSeconds(1));
        return T.Update(None, Mode.Gaming, t0.AddSeconds(30));
    }

    [Fact] // M5: after a crash mid auto-session, Auto mode still switches back by itself
    public void Resumed_auto_session_switches_back_after_quiet_delay()
    {
        T.Resume(automatic: true, returnWhenGameCloses: false, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(5)));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddSeconds(11)));
    }

    [Fact] // 1.2: "Switch back when my game closes" also works after clicking Start gaming yourself
    public void Manual_gaming_returns_after_the_game_closes_when_enabled()
    {
        T.ManualSwitch(Mode.Gaming, None, returnWhenGameCloses: true);
        Assert.Equal(AutoAction.None, T.Update(new[] { 7 }, Mode.Gaming, t0));
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddSeconds(10)));
        Assert.Equal(AutoAction.BackToCoding, T.Update(None, Mode.Gaming, t0.AddSeconds(11)));
    }

    [Fact] // 1.2: with no game ever seen there's nothing to wait for, so it never switches back by itself
    public void Manual_gaming_without_a_game_never_returns()
    {
        T.ManualSwitch(Mode.Gaming, None, returnWhenGameCloses: true);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddMinutes(5)));
    }

    [Fact] // 1.2: watching for the game to close must not start gaming when Auto mode is off
    public void Game_start_is_ignored_when_auto_start_is_off() =>
        Assert.Equal(AutoAction.None, T.Update(new[] { 7 }, Mode.Coding, t0, autoStart: false));

    [Fact]
    public void Manual_gaming_never_auto_returns()
    {
        T.ManualSwitch(Mode.Gaming, None);
        T.Update(new[] { 7 }, Mode.Gaming, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddMinutes(5)));
    }
}
