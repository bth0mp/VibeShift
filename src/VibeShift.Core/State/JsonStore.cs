using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeShift.Core.State;

/// <summary>settings.json, session.json and stats.json in one folder. Saves are atomic; unreadable files are set aside as *.bad.</summary>
public sealed class JsonStore(string dir)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // "Closed": null would load fine and then throw on every click; treat it as a corrupt file instead.
        RespectNullableAnnotations = true,
    };

    /// <summary>Set to keep a copy's data elsewhere (test runs, portable use) so it never touches the user's session.</summary>
    public const string DataDirVariable = "VIBESHIFT_DATA";

    public static JsonStore Default { get; } = new(ResolveDir(
        Environment.GetEnvironmentVariable(DataDirVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

    public static string ResolveDir(string? overrideDir, string appData) =>
        string.IsNullOrWhiteSpace(overrideDir) ? Path.Combine(appData, "VibeShift") : overrideDir;

    public string Dir => dir;

    private string SettingsPath => Path.Combine(dir, "settings.json");
    private string SessionPath => Path.Combine(dir, "session.json");

    public Settings LoadSettings()
    {
        var s = Load<Settings>(SettingsPath) ?? new Settings();
        s.Normalize();
        return s;
    }

    public void SaveSettings(Settings s) => Save(SettingsPath, s);

    public SessionState? LoadSession()
    {
        // Longer than for the other files: a session.json that can't be read starts VibeShift in vibe coding mode, and
        // what it closed is then never reopened.
        // ponytail: a lock of over 5 s still reads as no session; retrying from the poll would cover even that
        var s = Load<SessionState>(SessionPath, attempts: 25);
        s?.Normalize();
        return s;
    }

    public void SaveSession(SessionState s) => Save(SessionPath, s);

    public void ClearSession() => Retry(() => File.Delete(SessionPath));

    // Files that exist but couldn't be read (locked by antivirus, a backup or a sync tool), so defaults were used.
    private readonly HashSet<string> unread = [];

    /// <summary>Antivirus and backup tools hold a file for a moment: try again a few times before giving up.</summary>
    private static void Retry(Action io)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                io();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    private string StatsPath => Path.Combine(dir, "stats.json");

    /// <summary>Finished gaming sessions (stats.json), oldest first.</summary>
    public List<GamingRecord> LoadStats()
    {
        var all = Load<List<GamingRecord>>(StatsPath) ?? [];
        all.RemoveAll(r => r is null);
        return all;
    }

    /// <summary>Records a finished session, keeping the last 90 days.</summary>
    public void AddStat(GamingRecord record, DateTime nowUtc)
    {
        var all = LoadStats();
        all.RemoveAll(r => r.EndedUtc < nowUtc.AddDays(-90));
        all.Add(record);
        Save(StatsPath, all);
    }

    private T? Load<T>(string path, int attempts = 5) where T : class
    {
        if (!File.Exists(path)) return null;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            }
            catch (JsonException e)
            {
                try
                {
                    File.Move(path, path + ".bad", overwrite: true);
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    // can't set it aside; defaults still apply
                }
                Log.Write($"Unreadable {Path.GetFileName(path)} set aside as .bad: {e.Message}");
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked (e.g. by antivirus or a sync tool) isn't corrupt: retry briefly, then use defaults
                // and leave the file alone.
                if (attempt < attempts)
                {
                    Thread.Sleep(200);
                    continue;
                }
                Log.Write($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
                lock (unread) unread.Add(path); // the next save would replace it with those defaults: see Save
                return null;
            }
        }
    }

    private void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, Options);
            file.Flush(flushToDisk: true); // a power cut mid-game must not leave an empty session.json
        }
        bool keep;
        lock (unread) keep = unread.Contains(path);
        if (keep)
        {
            // Unmarked only once the copy is made. Still locked at the first save, the copy failed, the mark was gone
            // anyway, and the next save replaced the file with defaults after all (2026-11-23 review).
            KeepUnread(path);
            lock (unread) unread.Remove(path);
        }
        Retry(() => File.Move(tmp, path, overwrite: true));
    }

    /// <summary>
    /// This file was never read (it was locked), so what is about to replace it is defaults, not the user's choices.
    /// Keep the original as *.unread so nothing is lost for good. Throws if it can't: the save then fails and the
    /// original stays.
    /// </summary>
    private static void KeepUnread(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Retry(() => File.Copy(path, path + ".unread", overwrite: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Not replacing {Path.GetFileName(path)}: it couldn't be read earlier, and no copy could be kept ({e.Message})");
            throw;
        }
        Log.Write($"{Path.GetFileName(path)} couldn't be read earlier and is being replaced; the original is kept as .unread");
    }
}
