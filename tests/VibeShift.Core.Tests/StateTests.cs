using VibeShift.Core.Catalog;
using VibeShift.Core.State;

namespace VibeShift.Core.Tests;

public sealed class StateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeshift-test-" + Guid.NewGuid().ToString("N"));

    private JsonStore Store() => new(dir);

    public StateTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Missing_files_give_defaults()
    {
        var s = Store().LoadSettings();
        Assert.Equal(ThemeChoice.System, s.Theme);
        Assert.Equal(5, s.ForceCloseSeconds);
        Assert.Equal(new[] { "SysMain", "WSearch" }, s.BoostServices);
        Assert.False(s.AutoMode || s.ExtraBoost || s.StartWithWindows);
        Assert.Null(Store().LoadSession());
    }

    [Fact]
    public void Settings_and_session_round_trip()
    {
        var s = new Settings { Theme = ThemeChoice.Light, ForceCloseSeconds = 10 };
        s.Toggles["mcp"] = false;
        s.CustomApps.Add(@"C:\Tools\thing.exe");
        Store().SaveSettings(s);

        var session = new SessionState { Automatic = true, AvailBefore = 1, AvailAfter = 3, WslShutDown = true };
        session.Closed.Add(new ClosedApp("chatgpt", "ChatGPT", null, "OpenAI.ChatGPT!App"));
        session.ClosedEntryIds.Add("mcp");
        Store().SaveSession(session);

        var s2 = Store().LoadSettings();
        Assert.Equal(ThemeChoice.Light, s2.Theme);
        Assert.Equal(10, s2.ForceCloseSeconds);
        Assert.False(s2.Toggles["mcp"]);
        Assert.Equal(new[] { @"C:\Tools\thing.exe" }, s2.CustomApps);
        Assert.Contains("\"Light\"", File.ReadAllText(Path.Combine(dir, "settings.json"))); // enums as strings

        var r = Store().LoadSession()!;
        Assert.True(r.Automatic && r.WslShutDown);
        Assert.Equal(3, r.AvailAfter);
        Assert.Equal(session.Closed, r.Closed);
        Assert.Equal(new[] { "mcp" }, r.ClosedEntryIds);

        Store().ClearSession();
        Assert.Null(Store().LoadSession());
    }

    [Fact] // Review Focus 2
    public void Corrupt_files_are_set_aside_and_defaults_used()
    {
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"Theme\": ");
        File.WriteAllText(Path.Combine(dir, "session.json"), "garbage");
        Assert.Equal(5, Store().LoadSettings().ForceCloseSeconds);
        Assert.Null(Store().LoadSession());
        Assert.True(File.Exists(Path.Combine(dir, "settings.json.bad")));
        Assert.True(File.Exists(Path.Combine(dir, "session.json.bad")));
    }

    [Fact]
    public void Save_leaves_no_temp_file()
    {
        Store().SaveSettings(new Settings());
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void IsOn_falls_back_to_entry_default()
    {
        var s = new Settings();
        var mcp = AppCatalog.BuiltIn.Single(e => e.Id == "mcp");
        var vscode = AppCatalog.BuiltIn.Single(e => e.Id == "vscode");
        Assert.True(s.IsOn(mcp));
        Assert.False(s.IsOn(vscode));
        s.Toggles["vscode"] = true;
        Assert.True(s.IsOn(vscode));
    }

    [Fact]
    public void Log_rolls_over_at_1MB()
    {
        Log.Init(dir);
        var line = new string('x', 1000);
        for (int i = 0; i < 1100; i++) Log.Write(line);
        Assert.True(File.Exists(Path.Combine(dir, "vibeshift.log.old")));
        Assert.True(new FileInfo(Path.Combine(dir, "vibeshift.log")).Length < 1024 * 1024);
    }
}
