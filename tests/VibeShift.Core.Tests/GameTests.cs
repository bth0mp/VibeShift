using VibeShift.Core.Games;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Tests;

public class GameTests
{
    private const string Root = @"D:\SteamLibrary\steamapps\common";

    [Fact]
    public void Steam_vdf_paths_are_unescaped() =>
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" },
            GameLibraries.ParseSteamLibraryFolders("\"libraryfolders\"\n{\n\"0\"\n{\n\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n}\n\"1\"\n{\n\"path\"\t\t\"D:\\\\SteamLibrary\"\n}\n}"));

    [Fact]
    public void Epic_manifest_install_location() =>
        Assert.Equal(@"E:\Epic\Fortnite", GameLibraries.ParseEpicInstallLocation("{\"InstallLocation\": \"E:\\\\Epic\\\\Fortnite\"}"));

    private const string DeadlockAcf = "\"AppState\"\n{\n\t\"appid\"\t\t\"1422450\"\n\t\"name\"\t\t\"Deadlock\"\n\t\"StateFlags\"\t\t\"4\"\n" +
                                       "\t\"installdir\"\t\t\"Deadlock\"\n\t\"LastPlayed\"\t\t\"1761500000\"\n\t\"UserConfig\"\n\t{\n\t\t\"name\"\t\t\"ignored\"\n\t}\n}";

    [Fact] // 1.4 Play button: Steam's app manifests list installed games and when they were last played
    public void Steam_app_manifest_is_parsed()
    {
        var g = GameLibraries.ParseSteamAppManifest(DeadlockAcf)!;
        Assert.Equal("steam:1422450", g.Id);
        Assert.Equal("Deadlock", g.Name);
        Assert.Equal("steam://rungameid/1422450", g.LaunchUri);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761500000).UtcDateTime, g.LastPlayedUtc);
    }

    [Theory] // 1.4: Steam tools and half-installed games aren't offered to play
    [InlineData("\"AppState\"\n{\n\"appid\" \"228980\"\n\"name\" \"Steamworks Common Redistributables\"\n\"StateFlags\" \"4\"\n\"installdir\" \"Steamworks Shared\"\n}")]
    [InlineData("\"AppState\"\n{\n\"appid\" \"431960\"\n\"name\" \"Wallpaper Engine\"\n\"StateFlags\" \"4\"\n\"installdir\" \"wallpaper_engine\"\n}")]
    [InlineData("\"AppState\"\n{\n\"appid\" \"1493710\"\n\"name\" \"Proton Experimental\"\n\"StateFlags\" \"4\"\n\"installdir\" \"Proton - Experimental\"\n}")]
    [InlineData("\"AppState\"\n{\n\"appid\" \"1422450\"\n\"name\" \"Deadlock\"\n\"StateFlags\" \"1026\"\n\"installdir\" \"Deadlock\"\n}")]
    public void Steam_tools_and_partial_installs_are_skipped(string acf) => Assert.Null(GameLibraries.ParseSteamAppManifest(acf));

    [Fact] // 1.4: Epic games launch through the Epic launcher's own link
    public void Epic_game_is_parsed()
    {
        var g = GameLibraries.ParseEpicGame("{\"DisplayName\": \"Fortnite\", \"AppName\": \"Fortnite\", \"CatalogNamespace\": \"fn\", " +
                                            "\"CatalogItemId\": \"4fe75bbc\", \"AppCategories\": [\"games\"]}")!;
        Assert.Equal("epic:Fortnite", g.Id);
        Assert.Equal("com.epicgames.launcher://apps/fn%3A4fe75bbc%3AFortnite?action=launch&silent=true", g.LaunchUri);
        Assert.Null(GameLibraries.ParseEpicGame("{\"DisplayName\": \"Unreal\", \"AppName\": \"UE_5\", \"AppCategories\": [\"engines\"]}"));
    }

    [Fact] // review I3: engines and tools from Epic aren't games
    public void Epic_manifest_without_games_category_is_skipped()
    {
        Assert.Null(GameLibraries.ParseEpicInstallLocation("{\"InstallLocation\": \"E:\\\\UE_5.4\", \"AppCategories\": [\"engines\"]}"));
        Assert.Equal(@"D:\Epic Games\Fortnite",
            GameLibraries.ParseEpicInstallLocation("{\"InstallLocation\": \"D:\\\\Epic Games\\\\Fortnite\", \"AppCategories\": [\"games\", \"applications\"]}"));
    }

    [Theory] // review I3: creative tools sold on Steam aren't games
    [InlineData(@"D:\SteamLibrary\steamapps\common\Blender\blender.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Godot Engine\godot.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\RPG Maker MZ\RPGMZ.exe")]
    public void Steam_creative_tools_are_not_games(string path) =>
        Assert.False(GameRules.IsGame(path, new[] { Root }, Array.Empty<string>(), true));

    [Theory] // Review Focus 4
    [InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\game\bin\win64\deadlock.exe", true, true)]
    [InlineData(@"d:\steamlibrary\STEAMAPPS\common\Deadlock\deadlock.exe", true, true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\deadlock.exe", false, false)]
    [InlineData(@"D:\SteamLibrary\steamapps\common2\Other\other.exe", true, false)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\UnityCrashHandler64.exe", true, false)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper32.exe", true, false)]
    public void IsGame(string path, bool window, bool expected)
    {
        Assert.Equal(expected, GameRules.IsGame(path, new[] { Root }, Array.Empty<string>(), window));
        Assert.Equal(expected, GameRules.IsGame(path, new[] { Root + @"\" }, Array.Empty<string>(), window));
    }

    [Fact]
    public void My_games_count_anywhere_without_window() =>
        Assert.True(GameRules.IsGame(@"C:\Riot Games\VALORANT\live\VALORANT.exe", new[] { Root },
            new[] { @"c:\riot games\valorant\live\valorant.exe" }, false));

    [Fact] // 1.0.1 review #12: another signed-in user's game doesn't switch this user's apps
    public void Watcher_ignores_other_sessions()
    {
        var w = new GameWatcher(new[] { Root }, Array.Empty<string>(), _ => Root + @"\G\g.exe", _ => true, sessionId: 1);
        Assert.Empty(w.Scan(new[] { new ProcInfo(50, 1, "g", 10, 100, SessionId: 2) }));
        Assert.Equal(new[] { 51 }, w.Scan(new[] { new ProcInfo(51, 1, "g", 10, 100, SessionId: 1) }));
    }

    [Fact]
    public void Watcher_detects_game_once_its_window_appears_and_caches_paths()
    {
        int pathCalls = 0;
        bool window = false;
        var w = new GameWatcher(new[] { Root }, Array.Empty<string>(), _ => { pathCalls++; return Root + @"\G\g.exe"; }, _ => window);
        var snap = new[] { new ProcInfo(50, 1, "g", 10, 100) };
        Assert.Empty(w.Scan(snap));
        window = true;
        Assert.Equal(new[] { 50 }, w.Scan(snap));
        Assert.Equal(1, pathCalls);
    }
}
