using System.Globalization;

namespace VibeShift.Core;

public static class Format
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    /// <summary>"640 MB" under 1 GiB, otherwise "1.1 GB".</summary>
    public static string Bytes(long bytes) =>
        bytes < GiB
            ? (bytes / MiB).ToString(CultureInfo.InvariantCulture) + " MB"
            : (bytes / (double)GiB).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>"45 m", "2 h 5 m".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m" : $"{Math.Max(0, t.Minutes)} m";
}
