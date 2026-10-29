using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;
using VibeShift.Core.Switching;

namespace VibeShift.Core.Tests;

public class SwitchPlannerTests
{
    private const string Desk = @"C:\Program Files\WindowsApps\Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
    private const string Cli = @"C:\Users\u\AppData\Roaming\Claude\claude-code\2.1.308\c17a819afb40\claude.exe";

    private readonly Dictionary<int, string> paths = [];
    private readonly Dictionary<int, string> cmds = [];
    private readonly Settings settings = new();

    private static ProcInfo P(int pid, int ppid, string name, long ct = 1, long bytes = 10) => new(pid, ppid, name, bytes, ct);

    private IReadOnlyList<Detected> Detect(IReadOnlyList<ProcInfo> all, Func<ProcInfo, bool>? blocked = null) =>
        SwitchPlanner.Detect(all, AppCatalog.BuiltIn, pid => paths.GetValueOrDefault(pid), pid => cmds.GetValueOrDefault(pid), blocked ?? (_ => false));

    private GamingPlan Plan(IReadOnlyList<ProcInfo> all, bool automatic = false, int selfPid = 99_999, Func<ProcInfo, ProcInfo, bool>? spare = null) =>
        SwitchPlanner.PlanGaming(all, Detect(all), settings, automatic, selfPid, spare ?? ((_, _) => false));

    [Fact]
    public void Only_switched_on_running_rows_close()
    {
        var all = new[] { P(10, 0, "claude"), P(20, 0, "Code") };
        paths[10] = Desk;
        paths[20] = @"C:\x\Code.exe";
        Assert.Equal(new[] { "claude-desktop" }, Plan(all).Close.Select(c => c.Entry.Id));
    }

    [Fact]
    public void Two_cursor_instances_are_one_row_with_both_trees()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 1, "Cursor"), P(9, 0, "Cursor"), P(10, 9, "Cursor") };
        var cursor = Detect(all).Single();
        Assert.Equal("cursor", cursor.Entry.Id);
        Assert.Equal(new[] { 1, 9 }, cursor.Roots.Select(r => r.Pid).OrderBy(x => x));
        Assert.Equal(40, cursor.Bytes);
    }

    [Fact] // Review Focus 1, review I4: a half-killed app could never be reopened, so leave it whole
    public void App_that_VibeShift_runs_inside_is_left_alone()
    {
        var all = new[] { P(100, 0, "claude"), P(200, 100, "bash"), P(300, 200, "VibeShift"), P(201, 100, "node") };
        paths[100] = Desk;
        cmds[201] = "node mcp-server-x";
        var plan = Plan(all, selfPid: 300);
        Assert.DoesNotContain(plan.Close, c => c.Entry.Id == "claude-desktop");
        Assert.Equal(new[] { 201 }, plan.Close.Single(c => c.Entry.Id == "mcp").KillPids);
        Assert.Equal(10, plan.EstimatedBytes); // only the MCP server is counted
    }

    [Fact] // started from Claude Code's terminal: that shell (555) has exited, so only the shared job gives it away
    public void Sharing_a_job_with_an_app_it_closes_is_noticed()
    {
        var all = new[] { P(6, 0, "claude"), P(7, 6, "node"), P(300, 555, "VibeShift"), P(400, 0, "explorer") };
        paths[6] = Cli;
        cmds[7] = "node mcp-server-x";
        var detected = Detect(all);
        Assert.True(SwitchPlanner.SharesJobWithApp([6, 7, 300], 300, detected));
        Assert.False(SwitchPlanner.SharesJobWithApp([300, 400], 300, detected)); // a job of its own, from Explorer
        Assert.False(SwitchPlanner.SharesJobWithApp([], 300, detected)); // not in a job
    }

    [Fact] // audit A3: Steam started from Claude (or a steam:// link), minimised to the tray, with the game under it
    public void Game_launcher_under_a_closed_app_survives_with_everything_under_it()
    {
        var all = new[] { P(1, 0, "claude"), P(2, 1, "steam"), P(3, 2, "steamwebhelper"), P(4, 2, "deadlock"), P(5, 1, "claude") };
        paths[1] = paths[5] = Desk;
        Assert.Equal(new[] { 1, 5 }, Plan(all).Close.Single().KillPids);
    }

    [Fact] // review C1: the game and Windows processes are never killed, nor anything under them
    public void Kill_walk_skips_spared_processes_and_their_children()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 1, "conhost"), P(3, 2, "node"), P(4, 1, "deadlock"), P(5, 1, "Cursor") };
        var kill = Plan(all, spare: (node, _) => node.Pid is 2 or 4).Close.Single().KillPids;
        Assert.Equal(new[] { 1, 5 }, kill);
    }

    [Fact] // review I1: an app the user switched off survives even when a switched-on app started it
    public void Descendant_belonging_to_a_switched_off_row_is_spared()
    {
        var all = new[] { P(1, 0, "claude"), P(2, 1, "Code"), P(3, 2, "Code") };
        paths[1] = Desk;
        paths[2] = paths[3] = @"C:\x\Code.exe";
        Assert.Equal(new[] { 1 }, Plan(all).Close.Single(c => c.Entry.Id == "claude-desktop").KillPids);
    }

    [Theory] // review I1: a browser opened from Claude is its own app, not part of Claude
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", true, true)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\resources\helper.exe", true, false)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", false, false)]
    [InlineData(null, true, false)]
    public void Separate_windowed_app(string? path, bool hasWindow, bool expected) =>
        Assert.Equal(expected, SwitchPlanner.IsSeparateApp(path, Desk, hasWindow));

    [Fact] // M8: a supervisor killed first can't respawn the children killed after it
    public void Kill_list_is_parents_first()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 1, "Cursor"), P(3, 2, "Cursor") };
        Assert.Equal(new[] { 1, 2, 3 }, Plan(all).Close.Single().KillPids);
    }

    [Fact] // 1.0.1 review #3: parents first across rows too (an MCP child of VS Code dies after VS Code)
    public void Kill_order_is_parents_first_across_rows()
    {
        var all = new[] { P(1, 0, "Code"), P(2, 1, "node") };
        paths[1] = @"C:\x\Code.exe";
        cmds[2] = "node mcp-server-x";
        settings.Toggles["vscode"] = true;
        Assert.Equal(new[] { 1, 2 }, SwitchPlanner.KillOrder(all, Plan(all)));
    }

    [Fact] // 1.0.1 review #4: a row's own process is never spared as a "separate app"
    public void Rows_own_processes_are_never_spared_as_separate_apps()
    {
        var all = new[] { P(1, 0, "com.docker.backend"), P(2, 1, "Docker Desktop") };
        settings.Toggles["docker-wsl"] = true;
        var kill = Plan(all, spare: (node, _) => node.Name == "Docker Desktop").Close.Single().KillPids;
        Assert.Equal(new[] { 1, 2 }, kill);
    }

    [Fact] // 1.0.1 review #6: a Hyper-V "vmmem" isn't WSL's when vmmemWSL exists
    public void Hyper_V_vmmem_is_not_counted_next_to_vmmemWSL()
    {
        settings.Toggles["docker-wsl"] = true;
        var all = new[] { P(1, 0, "vmmemWSL", bytes: 1000), P(2, 0, "vmmem", bytes: 3000), P(3, 0, "wslhost") };
        Assert.Equal(1000, Plan(all).EstimatedBytes);
    }

    [Fact] // M7: MCP servers under Claude belong to both rows but free their RAM once
    public void Estimate_counts_each_process_once_even_when_rows_nest()
    {
        var all = new[] { P(1, 0, "claude", bytes: 100), P(2, 1, "node", bytes: 10) };
        paths[1] = Desk;
        cmds[2] = "node mcp-server-x";
        Assert.Equal(110, Plan(all).EstimatedBytes);
    }

    [Fact] // M9: Docker Desktop can sit under its backend; it's still the one to reopen
    public void Launcher_below_a_helper_is_still_reopened()
    {
        var all = new[] { P(1, 0, "com.docker.backend"), P(2, 1, "Docker Desktop") };
        settings.Toggles["docker-wsl"] = true;
        Assert.Equal(new[] { 2 }, Plan(all).Close.Single().ReopenRoots.Select(r => r.Pid));
    }

    [Fact] // M9: the Microsoft 365 Copilot app is its own row, reopened like the rest
    public void Microsoft_365_Copilot_is_reopened() =>
        Assert.Equal(new[] { 1 }, Plan(new[] { P(1, 0, "M365Copilot") }).Close.Single().ReopenRoots.Select(r => r.Pid));

    [Fact] // M10: older Windows 10 WSL runs its VM as "vmmem"
    public void Older_wsl_vm_counts_only_while_wsl_is_running()
    {
        settings.Toggles["docker-wsl"] = true;
        var plan = Plan(new[] { P(1, 0, "vmmem", bytes: 1000), P(2, 0, "wslhost") });
        Assert.True(plan.ShutdownWsl);
        Assert.Equal(1000, plan.EstimatedBytes);
        Assert.False(Plan(new[] { P(1, 0, "vmmem", bytes: 1000) }).ShutdownWsl); // a Hyper-V VM, not WSL
    }

    [Fact]
    public void Automatic_switch_skips_polite_close_and_boost()
    {
        var all = new[] { P(1, 0, "Cursor") };
        settings.ExtraBoost = true;
        Assert.Empty(Plan(all, automatic: true).StopServices);
        Assert.False(Plan(all, automatic: true).PoliteClose);
        Assert.True(Plan(all, automatic: false).PoliteClose);
        Assert.Equal(new[] { "SysMain", "WSearch" }, Plan(all, automatic: false).StopServices);
    }

    [Fact]
    public void Docker_wsl_row_on_closes_docker_and_shuts_down_wsl_off_does_neither()
    {
        var all = new[] { P(1, 0, "Docker Desktop"), P(2, 0, "vmmemWSL", bytes: 1000) };
        var dw = Detect(all).Single(d => d.Entry.Id == "docker-wsl");
        Assert.Equal(new[] { 1 }, dw.Roots.Select(r => r.Pid));
        Assert.Equal(1010, dw.Bytes);

        var off = Plan(all);
        Assert.Empty(off.Close);
        Assert.False(off.ShutdownWsl);

        settings.Toggles["docker-wsl"] = true;
        var on = Plan(all);
        Assert.Equal(new[] { 1 }, on.Close.Single().KillPids);
        Assert.True(on.ShutdownWsl);
        Assert.Equal(1010, on.EstimatedBytes);
    }

    [Fact]
    public void Wsl_alone_is_detected_and_shut_down_without_anything_to_kill()
    {
        var all = new[] { P(2, 0, "vmmemWSL", bytes: 1000) };
        settings.Toggles["docker-wsl"] = true;
        var plan = Plan(all);
        Assert.Empty(plan.Close);
        Assert.True(plan.ShutdownWsl);
        Assert.Equal(1000, plan.EstimatedBytes);
        Assert.Equal(new[] { "docker-wsl" }, plan.ClosedEntryIds); // M10: the row shows it was shut down
    }

    [Fact]
    public void Only_launcher_roots_of_reopenable_entries_are_recorded()
    {
        var all = new[] { P(1, 0, "ollama app"), P(2, 0, "ollama"), P(5, 0, "node"), P(6, 0, "claude") };
        cmds[5] = "node mcp-server-x";
        paths[6] = Cli;
        settings.Toggles["cli-agents"] = true;
        var close = Plan(all).Close.ToDictionary(c => c.Entry.Id);
        Assert.Equal(new[] { 1 }, close["ollama"].ReopenRoots.Select(r => r.Pid));
        Assert.Empty(close["mcp"].ReopenRoots);
        Assert.Empty(close["cli-agents"].ReopenRoots);
    }

    [Fact] // Review Focus 5
    public void Reopen_skips_apps_already_running()
    {
        var session = new SessionState
        {
            Closed = { new("cursor", "Cursor", @"C:\c\Cursor.exe", null), new("chatgpt", "ChatGPT", null, "OpenAI.ChatGPT_x!App") },
        };
        var running = Detect(new[] { P(1, 0, "Cursor") });
        Assert.Equal(new[] { "chatgpt" }, SwitchPlanner.PlanReopen(session, running).Select(c => c.EntryId));
    }

    [Fact]
    public void Blocked_processes_are_never_detected() =>
        Assert.DoesNotContain(Detect(new[] { P(5, 0, "Cursor") }, blocked: p => p.Pid == 5), d => d.Entry.Id == "cursor");

    [Fact] // 1.1: a row covering several browsers reopens whichever one was running
    public void Browser_row_reopens_whichever_browser_was_running()
    {
        settings.Toggles["browsers"] = true;
        Assert.Equal(new[] { 1 }, Plan(new[] { P(1, 0, "chrome"), P(2, 1, "chrome") }).Close.Single().ReopenRoots.Select(r => r.Pid));
    }

    [Fact] // 1.1: Wallpaper Engine runs as 32- or 64-bit; either one is reopened
    public void Wallpaper_engine_64_bit_is_reopened()
    {
        settings.Toggles["wallpaper-engine"] = true;
        Assert.Equal(new[] { 1 }, Plan(new[] { P(1, 0, "wallpaper64") }).Close.Single().ReopenRoots.Select(r => r.Pid));
    }

    [Fact] // 1.1: Windows restarts Widgets on its own, so it's closed but not reopened
    public void Widgets_are_not_reopened()
    {
        settings.Toggles["widgets"] = true;
        Assert.Empty(Plan(new[] { P(1, 0, "Widgets") }).Close.Single().ReopenRoots);
    }

    [Fact] // 1.1: a browser told to close politely forgets its tabs; one that's ended restores them on reopen
    public void Browsers_are_not_asked_to_close_politely()
    {
        settings.Toggles["browsers"] = true;
        var plan = Plan(new[] { P(1, 0, "firefox"), P(2, 0, "Cursor") });
        Assert.Equal(new[] { 2 }, plan.PoliteCloseRoots.Select(r => r.Pid));
        Assert.Empty(Plan(new[] { P(1, 0, "firefox"), P(2, 0, "Cursor") }, automatic: true).PoliteCloseRoots);
    }

    [Fact] // 1.1 "What's using RAM": other apps grouped by exe, biggest first
    public void Suggestions_group_other_apps_by_exe()
    {
        var all = new[]
        {
            P(1, 0, "Spotify", bytes: 3_000), P(2, 1, "Spotify", bytes: 2_000), // one app, two processes
            P(3, 0, "Cursor", bytes: 9_000),                                 // already a row
            P(4, 0, "blender", bytes: 4_000),
            P(5, 0, "tiny", bytes: 50),                                      // below the threshold
            P(6, 0, "secret", bytes: 8_000),                                 // excluded by the caller
        };
        paths[1] = paths[2] = @"C:\Spotify\Spotify.exe";
        paths[3] = @"C:\Cursor\Cursor.exe";
        paths[4] = @"C:\Blender\blender.exe";
        paths[5] = @"C:\Tiny\tiny.exe";
        paths[6] = @"C:\Secret\secret.exe";
        var s = SwitchPlanner.SuggestApps(all, Detect(all), pid => paths.GetValueOrDefault(pid), (p, _) => p.Pid == 6, max: 5, minBytes: 1_000);
        Assert.Equal(new[] { (@"C:\Spotify\Spotify.exe", 5_000L), (@"C:\Blender\blender.exe", 4_000L) }, s.Select(x => (x.Path, x.Bytes)));
    }

    [Fact] // 1.2 vibe apps: opened on the way back even if they weren't closed for gaming
    public void Vibe_apps_open_even_if_they_were_not_closed()
    {
        var session = new SessionState { Closed = { new("cursor", "Cursor", @"C:\c\Cursor.exe", null) } };
        var known = new Dictionary<string, ClosedApp>
        {
            ["claude-desktop"] = new("claude-desktop", "Claude", null, "Claude_pzs8sxrjxfjjc!Claude"),
            ["chatgpt"] = new("chatgpt", "ChatGPT", null, "OpenAI.ChatGPT_x!App"),
        };
        var running = Detect(new[] { P(1, 0, "ChatGPT") }); // already open: not launched twice
        var plan = SwitchPlanner.PlanVibeLaunch(session, running, new[] { "claude-desktop", "chatgpt" }, known);
        Assert.Equal(new[] { "cursor", "claude-desktop" }, plan.Select(a => a.EntryId));
    }

    [Fact] // 1.2: an app never seen running has no launcher on record yet, so it can't be opened
    public void Vibe_app_without_a_known_launcher_is_skipped() =>
        Assert.Empty(SwitchPlanner.PlanVibeLaunch(new SessionState(), [], new[] { "cursor" }, new Dictionary<string, ClosedApp>()));

    [Fact] // 1.2: an app closed for gaming and also a vibe app is opened once
    public void Closed_vibe_app_is_opened_once()
    {
        var session = new SessionState { Closed = { new("cursor", "Cursor", @"C:\c\Cursor.exe", null) } };
        var known = new Dictionary<string, ClosedApp> { ["cursor"] = new("cursor", "Cursor", @"C:\c\Cursor.exe", null) };
        Assert.Single(SwitchPlanner.PlanVibeLaunch(session, [], new[] { "cursor" }, known));
    }

    [Fact] // 1.2: custom apps can always be launched from their own path
    public void Custom_apps_have_a_launch_target_from_their_path()
    {
        var s = new Settings { CustomApps = { @"C:\Tools\thing.exe" } };
        var target = Switcher.LaunchTargets(s)[AppCatalog.Custom(@"C:\Tools\thing.exe").Id];
        Assert.Equal(@"C:\Tools\thing.exe", target.ExePath);
    }

    [Fact] // 1.2: a launcher VibeShift saw an added app use (e.g. a Store app's AUMID) beats its raw exe path
    public void Recorded_launcher_wins_over_a_custom_apps_path()
    {
        var id = AppCatalog.Custom(@"C:\Store\paint.exe").Id;
        var s = new Settings { CustomApps = { @"C:\Store\paint.exe" } };
        s.LaunchTargets[id] = new ClosedApp(id, "Paint", @"C:\Store\paint.exe", "Microsoft.Paint_8wekyb3d8bbwe!App");
        Assert.Equal("Microsoft.Paint_8wekyb3d8bbwe!App", Switcher.LaunchTargets(s)[id].Aumid);
    }

    [Theory] // 1.3 Vibe page: AI apps (and anything you added) get a live switch; plumbing doesn't
    [InlineData("claude-desktop", true)]
    [InlineData("chatgpt", true)]
    [InlineData("ollama", true)]
    [InlineData("m365copilot", true)]
    [InlineData("mcp", false)]
    [InlineData("docker-wsl", false)]
    [InlineData("browsers", false)]
    [InlineData("cli-agents", false)]
    public void Vibe_page_lists_ai_apps(string id, bool expected) =>
        Assert.Equal(expected, AppCatalog.IsAiApp(AppCatalog.BuiltIn.Single(e => e.Id == id)));

    [Fact]
    public void Added_apps_are_on_the_vibe_page_too() => Assert.True(AppCatalog.IsAiApp(AppCatalog.Custom(@"C:\Tools\thing.exe")));

    [Fact] // 1.3: closing one app from the Vibe page touches nothing else, whatever the gaming toggles say
    public void Closing_from_the_vibe_page_closes_only_the_chosen_app()
    {
        var all = new[] { P(1, 0, "claude"), P(2, 0, "Cursor"), P(3, 0, "node"), P(4, 0, "vmmemWSL") };
        paths[1] = Desk;
        cmds[3] = "node mcp-server-x";
        settings.Toggles["docker-wsl"] = true;
        settings.ExtraBoost = true;
        var plan = SwitchPlanner.PlanGaming(all, Detect(all), settings.ForClosing(new[] { "cursor" }), automatic: false, 99_999, (_, _) => false);
        Assert.Equal(new[] { "cursor" }, plan.Close.Select(c => c.Entry.Id)); // not Claude, not MCP, though both are on for gaming
        Assert.False(plan.ShutdownWsl);
        Assert.Empty(plan.StopServices);
        Assert.True(settings.IsOn(AppCatalog.BuiltIn.Single(e => e.Id == "claude-desktop"))); // the real settings are untouched
    }

    [Fact] // 1.4 game-time apps: an app you open for gaming is never closed for gaming
    public void Game_time_apps_are_never_closed()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 0, "claude") };
        paths[2] = Desk;
        settings.GameApps.Add("cursor");
        Assert.Equal(new[] { "claude-desktop" }, Plan(all).Close.Select(c => c.Entry.Id));
    }

    [Fact] // M12d: case-variant custom paths are one app, not a crash
    public void Custom_paths_differing_only_in_case_are_one_entry()
    {
        var s = new Settings { CustomApps = { @"C:\A\x.exe", @"c:\a\X.EXE" } };
        Assert.Single(Switcher.Entries(s), e => e.Kind == EntryKind.Custom);
    }

    [Theory] // M9: Squirrel-installed apps update into a new app-x.y.z folder; reopen the stable stub
    [InlineData(@"C:\U\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe", true, @"C:\U\AppData\Local\AnthropicClaude\claude.exe")]
    [InlineData(@"C:\U\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe", false, @"C:\U\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe")]
    [InlineData(@"C:\Apps\cursor\Cursor.exe", true, @"C:\Apps\cursor\Cursor.exe")]
    public void Versioned_install_reopens_through_its_stub(string path, bool stubExists, string expected) =>
        Assert.Equal(expected, ReopenTarget.PreferStub(path, _ => stubExists));

    // By full path: a bare "explorer.exe" is searched for in the current folder (a terminal's, maybe a cloned repo) first.
    private static readonly string WindowsExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [Fact] // M14: an elevated VibeShift must still reopen apps unelevated, via the shell
    public void Elevated_reopen_goes_through_the_shell()
    {
        var b = ReopenTarget.ToStartInfo(new("cursor", "Cursor", @"C:\Apps\cursor\Cursor.exe", null), elevated: true);
        Assert.Equal(WindowsExplorer, b.FileName);
        Assert.Equal("\"C:\\Apps\\cursor\\Cursor.exe\"", b.Arguments);
    }

    [Fact]
    public void Reopen_start_info()
    {
        var a = ReopenTarget.ToStartInfo(new("chatgpt", "ChatGPT", null, "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT"));
        Assert.Equal(WindowsExplorer, a.FileName);
        Assert.Equal(@"shell:AppsFolder\OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT", a.Arguments);
        var b = ReopenTarget.ToStartInfo(new("cursor", "Cursor", @"C:\Apps\cursor\Cursor.exe", null));
        Assert.Equal(@"C:\Apps\cursor\Cursor.exe", b.FileName);
        Assert.True(b.UseShellExecute);
        Assert.Equal(@"C:\Apps\cursor", b.WorkingDirectory);
    }
}
