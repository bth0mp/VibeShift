using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeShift.Core.State;

/// <summary>settings.json and session.json in one folder. Saves are atomic; unreadable files are set aside as *.bad.</summary>
public sealed class JsonStore(string dir)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static JsonStore Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeShift"));

    public string Dir => dir;

    private string SettingsPath => Path.Combine(dir, "settings.json");
    private string SessionPath => Path.Combine(dir, "session.json");

    public Settings LoadSettings() => Load<Settings>(SettingsPath) ?? new Settings();

    public void SaveSettings(Settings s) => Save(SettingsPath, s);

    public SessionState? LoadSession() => Load<SessionState>(SessionPath);

    public void SaveSession(SessionState s) => Save(SessionPath, s);

    public void ClearSession() => File.Delete(SessionPath);

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException e)
        {
            File.Move(path, path + ".bad", overwrite: true);
            Log.Write($"Unreadable {Path.GetFileName(path)} moved to .bad: {e.Message}");
            return null;
        }
    }

    private void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
