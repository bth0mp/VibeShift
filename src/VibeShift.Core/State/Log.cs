namespace VibeShift.Core.State;

/// <summary>Short rolling text log of switches and failures, for GitHub issue reports. No-op until Init.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? path;

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        lock (Gate) path = Path.Combine(dir, "vibeshift.log");
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (path is null) return;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // logging must never take the app down
            }
        }
    }
}
