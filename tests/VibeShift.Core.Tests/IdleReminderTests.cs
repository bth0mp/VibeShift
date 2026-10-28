using VibeShift.Core.Games;

namespace VibeShift.Core.Tests;

public sealed class IdleReminderTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly IdleReminder reminder = new(TimeSpan.FromHours(2));

    [Fact]
    public void Reminds_once_when_no_game_has_run_for_two_hours()
    {
        Assert.False(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(1.9)));
        Assert.True(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(2)));
        Assert.False(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(5)));
    }

    [Fact]
    public void Counts_from_when_the_game_was_last_seen()
    {
        Assert.False(reminder.Due(gameRunning: true, canNotify: true, Start, Start.AddHours(3)));
        Assert.False(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(4.9)));
        Assert.True(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(5)));
    }

    [Fact] // a full-screen app (maybe a game VibeShift doesn't know) or quiet hours: wait, don't lose the reminder
    public void Waits_for_a_moment_windows_accepts_notifications()
    {
        Assert.False(reminder.Due(gameRunning: false, canNotify: false, Start, Start.AddHours(3)));
        Assert.True(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(3.1)));
    }

    [Fact]
    public void A_new_session_can_remind_again()
    {
        Assert.True(reminder.Due(gameRunning: false, canNotify: true, Start, Start.AddHours(2)));
        reminder.Reset();
        var next = Start.AddHours(10);
        Assert.False(reminder.Due(gameRunning: false, canNotify: true, next, next.AddHours(1)));
        Assert.True(reminder.Due(gameRunning: false, canNotify: true, next, next.AddHours(2)));
    }
}
