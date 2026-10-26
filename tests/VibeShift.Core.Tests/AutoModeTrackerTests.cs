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

    [Fact]
    public void Manual_gaming_never_auto_returns()
    {
        T.ManualSwitch(Mode.Gaming, None);
        T.Update(new[] { 7 }, Mode.Gaming, t0);
        Assert.Equal(AutoAction.None, T.Update(None, Mode.Gaming, t0.AddMinutes(5)));
    }
}
