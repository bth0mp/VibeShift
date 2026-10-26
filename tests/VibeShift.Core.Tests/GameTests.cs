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
