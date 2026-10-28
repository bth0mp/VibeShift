using VibeShift.Core.State;

namespace VibeShift.Core.Tests;

public sealed class StatsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeshift-test-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    public StatsTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private const long GB = 1L << 30;

    [Fact]
    public void Week_sums_the_last_seven_days_only()
    {
        var records = new[]
        {
            new GamingRecord(now.AddDays(-1).AddHours(-2), now.AddDays(-1), 3 * GB),
            new GamingRecord(now.AddDays(-3).AddMinutes(-75), now.AddDays(-3), GB),
            new GamingRecord(now.AddDays(-9).AddHours(-5), now.AddDays(-9), 9 * GB), // older than a week
        };
        Assert.Equal("Last 7 days: 2 sessions · 4.0 GB freed · 3 h 15 m in gaming mode", Stats.Week(records, now));
    }

    [Fact]
    public void Week_is_empty_without_recent_sessions() =>
        Assert.Equal("", Stats.Week([new GamingRecord(now.AddDays(-20), now.AddDays(-20), GB)], now));

    [Fact]
    public void Week_says_session_for_one() =>
        Assert.StartsWith("Last 7 days: 1 session ·", Stats.Week([new GamingRecord(now.AddMinutes(-40), now, GB / 2)], now));

    [Theory]
    [InlineData(0, "0 m")]
    [InlineData(45, "45 m")]
    [InlineData(60, "1 h 0 m")]
    [InlineData(1565, "26 h 5 m")]
    public void Durations_read_in_hours_and_minutes(int minutes, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Store_appends_records_and_drops_ones_older_than_90_days()
    {
        var store = new JsonStore(dir);
        Assert.Empty(store.LoadStats());
        store.AddStat(new GamingRecord(now.AddDays(-100), now.AddDays(-100), GB), now.AddDays(-100));
        store.AddStat(new GamingRecord(now.AddHours(-1), now, 2 * GB), now);
        var all = store.LoadStats();
        Assert.Equal(2 * GB, Assert.Single(all).FreedBytes);
    }

    [Fact]
    public void Corrupt_stats_file_starts_fresh()
    {
        File.WriteAllText(Path.Combine(dir, "stats.json"), "[{ \"Started");
        var store = new JsonStore(dir);
        Assert.Empty(store.LoadStats());
        store.AddStat(new GamingRecord(now.AddHours(-1), now, GB), now);
        Assert.Single(store.LoadStats());
    }
}
