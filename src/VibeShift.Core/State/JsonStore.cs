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
        var s = Load<SessionState>(SessionPath);
        s?.Normalize();
        return s;
    }

    public void SaveSession(SessionState s) => Save(SessionPath, s);

    public void ClearSession() => File.Delete(SessionPath);

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

    private static T? Load<T>(string path) where T : class
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
                if (attempt < 5)
                {
                    Thread.Sleep(200);
                    continue;
                }
                Log.Write($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
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
        File.Move(tmp, path, overwrite: true);
    }
}
