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

    [Fact] // M5: after a crash mid auto-session, Auto mode still switches back by itself
    public void Resumed_auto_session_switches_back_after_quiet_delay()
    {
        T.ResumeAutoSession(t0);
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
