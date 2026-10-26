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
}
