namespace VibeShift.Core.State;

/// <summary>One finished gaming session, for the Home page's "last 7 days" line.</summary>
public sealed record GamingRecord(DateTime StartedUtc, DateTime EndedUtc, long FreedBytes);

public static class Stats
{
    /// <summary>"Last 7 days: 3 sessions · 4.2 GB freed · 5 h 10 m in gaming mode", or "" when there were none.</summary>
    public static string Week(IEnumerable<GamingRecord> records, DateTime nowUtc)
    {
        var week = records.Where(r => r.EndedUtc > nowUtc.AddDays(-7)).ToList();
        if (week.Count == 0) return "";
        var time = TimeSpan.FromTicks(week.Sum(r => Math.Max(0, (r.EndedUtc - r.StartedUtc).Ticks)));
        return $"Last 7 days: {week.Count} session{(week.Count == 1 ? "" : "s")} · {Format.Bytes(week.Sum(r => r.FreedBytes))} freed · {Format.Duration(time)} in gaming mode";
    }
}
