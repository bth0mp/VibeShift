using System.ComponentModel;
using System.Diagnostics;
using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>
/// Carries out SwitchPlanner's decisions. Every step and every process is attempted on its own:
/// one failure is logged and skipped, never allowed to stop the rest.
/// </summary>
public sealed class Switcher(JsonStore store)
{
    /// <summary>The user's own apps first, so a custom exe that shares a name with a built-in wins.</summary>
    public static IReadOnlyList<CatalogEntry> Entries(Settings s) =>
        s.CustomApps.Select(AppCatalog.Custom).Concat(AppCatalog.BuiltIn).DistinctBy(e => e.Id).ToList();

    public Task<SessionState> StartGamingAsync(Settings s, bool automatic, IReadOnlyCollection<int> gamePids, IProgress<string>? progress) =>
        Task.Run(() => StartGaming(s, automatic, gamePids, progress));

    public Task<IReadOnlyList<string>> BackToCodingAsync(Settings s, SessionState session, IProgress<string>? progress) =>
        Task.Run(() => BackToCoding(s, session, progress));

    private SessionState StartGaming(Settings s, bool automatic, IReadOnlyCollection<int> gamePids, IProgress<string>? progress)
    {
        var session = new SessionState { Automatic = automatic, AvailBefore = ProcessQuery.AvailableRamBytes() };
        var all = ProcessSnapshot.Take();
        int self = Environment.ProcessId;
        var games = gamePids.ToHashSet();
        var detected = SwitchPlanner.Detect(all, Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, games));
        // Never reach the game (or anything it started), Windows' own processes, or separate windowed apps.
        var gameTree = games.SelectMany(g => ProcessTree.Descendants(all, g)).Select(p => p.Pid).ToHashSet();
        var plan = SwitchPlanner.PlanGaming(all, detected, s, automatic, self, (node, root) =>
        {
            if (gameTree.Contains(node.Pid)) return true;
            var path = ProcessQuery.GetImagePath(node.Pid);
            return BlockList.IsBlocked(node.Name, path, node.Pid, self, games)
                || SwitchPlanner.IsSeparateApp(path, ProcessQuery.GetImagePath(root.Pid), ProcessQuery.HasVisibleWindow(node.Pid));
        });

        foreach (var target in plan.Close)
        {
            session.ClosedEntryIds.Add(target.Entry.Id);
            foreach (var root in target.ReopenRoots)
            {
                var app = new ClosedApp(target.Entry.Id, target.Entry.Name, ProcessQuery.GetImagePath(root.Pid), ProcessQuery.GetAumid(root.Pid));
                if ((app.ExePath ?? app.Aumid) is not null && !session.Closed.Contains(app)) session.Closed.Add(app);
            }
        }
        session.WslShutDown = plan.ShutdownWsl;
        store.SaveSession(session); // before anything is closed, so a crash mid-switch can still switch back

        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in all) byPid.TryAdd(p.Pid, p);

        if (plan.Close.Count > 0)
        {
            int n = plan.Close.Count;
            progress?.Report($"Closing {n} app{(n == 1 ? "" : "s")}...");
            if (plan.PoliteClose) PoliteClose(plan.Close.SelectMany(t => t.Roots), s.ForceCloseSeconds);
            foreach (var pid in plan.Close.SelectMany(t => t.KillPids))
                if (byPid.TryGetValue(pid, out var info)) Kill(info);
        }

        if (plan.ShutdownWsl)
        {
            progress?.Report("Shutting down WSL...");
            Step("wsl --shutdown", () => RunHidden("wsl.exe", "--shutdown"));
        }

        if (plan.StopServices.Count > 0)
        {
            progress?.Report("Pausing Windows services...");
            var running = plan.StopServices.Where(Services.IsRunning).ToList();
            if (running.Count > 0)
            {
                Step("stop services", () => Services.RunElevated("stop", running));
                session.StoppedServices.AddRange(running.Where(name => !Services.IsRunning(name)));
            }
        }

        // ponytail: fixed 1 s settle before measuring; poll free RAM until stable if FREED reads low
        Thread.Sleep(1000);
        session.AvailAfter = ProcessQuery.AvailableRamBytes();
        store.SaveSession(session);
        Log.Write($"Gaming mode ({(automatic ? "auto" : "manual")}): closed [{string.Join(", ", session.ClosedEntryIds)}], " +
                  $"services [{string.Join(", ", session.StoppedServices)}], freed {Format.Bytes(Math.Max(0, session.AvailAfter - session.AvailBefore))}");
        return session;
    }

    private IReadOnlyList<string> BackToCoding(Settings s, SessionState session, IProgress<string>? progress)
    {
        if (session.StoppedServices.Count > 0)
        {
            progress?.Report("Restarting Windows services...");
            Step("start services", () => Services.RunElevated("start", session.StoppedServices));
        }

        var running = SwitchPlanner.Detect(ProcessSnapshot.Take(), Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine, _ => false);
        var toOpen = SwitchPlanner.PlanReopen(session, running);
        var failed = new List<string>();
        if (toOpen.Count > 0) progress?.Report($"Reopening {toOpen.Count} app{(toOpen.Count == 1 ? "" : "s")}...");
        foreach (var app in toOpen)
        {
            try
            {
                using var _ = Process.Start(ReopenTarget.ToStartInfo(app));
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or FileNotFoundException)
            {
                failed.Add(app.Name);
                Log.Write($"Couldn't reopen {app.Name}: {e.Message}");
            }
        }

        store.ClearSession();
        Log.Write($"Vibe coding mode: reopened {toOpen.Count - failed.Count} of {toOpen.Count}");
        return failed;
    }

    private static void PoliteClose(IEnumerable<ProcInfo> roots, int seconds)
    {
        var procs = roots.Select(Open).OfType<Process>().ToList();
        foreach (var p in procs) Step($"close {p.ProcessName}", () => p.CloseMainWindow());
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && procs.Any(p => !HasExited(p))) Thread.Sleep(250);
        foreach (var p in procs) p.Dispose();
    }

    /// <summary>Kills only if the PID still belongs to the process in the snapshot (PIDs get reused).</summary>
    private static void Kill(ProcInfo info)
    {
        using var p = Open(info);
        if (p is null) return;
        Step($"kill {info.Name} ({info.Pid})", () => p.Kill());
    }

    private static Process? Open(ProcInfo info)
    {
        try
        {
            var p = Process.GetProcessById(info.Pid);
            if (p.StartTime.ToFileTimeUtc() == info.CreateTime) return p;
            p.Dispose();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // already gone, or not ours to touch
        }
        return null;
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return true; }
    }

    private static void RunHidden(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(30_000);
    }

    private static void Step(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Write($"{label} failed: {e.Message}");
        }
    }
}
