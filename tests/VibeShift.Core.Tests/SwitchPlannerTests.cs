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

    private GamingPlan Plan(IReadOnlyList<ProcInfo> all, bool automatic = false, int selfPid = 99_999) =>
        SwitchPlanner.PlanGaming(all, Detect(all), settings, automatic, selfPid);

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

    [Fact] // Review Focus 1
    public void Kill_list_spares_self_and_its_parent_chain()
    {
        var all = new[] { P(100, 0, "claude"), P(200, 100, "bash"), P(300, 200, "VibeShift"), P(201, 100, "node") };
        paths[100] = Desk;
        cmds[201] = "node mcp-server-x";
        var kill = Plan(all, selfPid: 300).Close.Single(c => c.Entry.Id == "claude-desktop").KillPids;
        Assert.DoesNotContain(100, kill);
        Assert.DoesNotContain(200, kill);
        Assert.DoesNotContain(300, kill);
        Assert.Contains(201, kill);
    }

    [Fact]
    public void Kill_list_is_children_first()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 1, "Cursor"), P(3, 2, "Cursor") };
        Assert.Equal(new[] { 3, 2, 1 }, Plan(all).Close.Single().KillPids);
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

    [Fact]
    public void Reopen_start_info()
    {
        var a = ReopenTarget.ToStartInfo(new("chatgpt", "ChatGPT", null, "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT"));
        Assert.Equal("explorer.exe", a.FileName);
        Assert.Equal(@"shell:AppsFolder\OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT", a.Arguments);
        var b = ReopenTarget.ToStartInfo(new("cursor", "Cursor", @"C:\Apps\cursor\Cursor.exe", null));
        Assert.Equal(@"C:\Apps\cursor\Cursor.exe", b.FileName);
        Assert.True(b.UseShellExecute);
        Assert.Equal(@"C:\Apps\cursor", b.WorkingDirectory);
    }
}
