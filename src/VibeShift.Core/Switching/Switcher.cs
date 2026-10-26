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
        int mySession = MySessionId();
        var games = gamePids.ToHashSet();
        // Other users' processes are never ours to touch, even when VibeShift runs elevated.
        var detected = SwitchPlanner.Detect(all, Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != mySession || BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, games));
        // Never reach the game (or anything it started), Windows' own processes, or separate windowed apps.
        var gameTree = games.SelectMany(g => ProcessTree.Descendants(all, g)).Select(p => p.Pid).ToHashSet();
        var plan = SwitchPlanner.PlanGaming(all, detected, s, automatic, self, (node, root) =>
        {
            if (gameTree.Contains(node.Pid) || node.SessionId != mySession) return true;
            var path = ProcessQuery.GetImagePath(node.Pid);
            return BlockList.IsBlocked(node.Name, path, node.Pid, self, games)
                || SwitchPlanner.IsSeparateApp(path, ProcessQuery.GetImagePath(root.Pid), ProcessQuery.HasVisibleWindow(node.Pid));
        });

        session.ClosedEntryIds.AddRange(plan.ClosedEntryIds);
        foreach (var target in plan.Close)
        {
            foreach (var root in target.ReopenRoots)
            {
                var exe = ProcessQuery.GetImagePath(root.Pid) is { } path ? ReopenTarget.PreferStub(path, File.Exists) : null;
                var app = new ClosedApp(target.Entry.Id, target.Entry.Name, exe, ProcessQuery.GetAumid(root.Pid));
                if ((app.ExePath ?? app.Aumid) is not null && !session.Closed.Contains(app)) session.Closed.Add(app);
            }
        }
        session.WslShutDown = plan.ShutdownWsl;
        store.SaveSession(session); // before anything is closed, so a crash mid-switch can still switch back

        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in all) byPid.TryAdd(p.Pid, p);

        var targets = SwitchPlanner.KillOrder(all, plan).Where(byPid.ContainsKey).Select(pid => byPid[pid]).ToList();
        if (plan.Close.Count > 0)
        {
            int n = plan.Close.Count;
            progress?.Report($"Closing {n} app{(n == 1 ? "" : "s")}...");
            if (plan.PoliteClose) PoliteClose(plan.Close.SelectMany(t => t.Roots), s.ForceCloseSeconds);
            foreach (var info in targets) Kill(info);
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
                store.SaveSession(session); // record paused services at once, so they're restarted whatever happens next
            }
        }

        // ponytail: fixed 1 s settle before measuring; poll free RAM until stable if FREED reads low
        Thread.Sleep(1000);
        session.AvailAfter = ProcessQuery.AvailableRamBytes();
        session.KilledBytes = targets.Where(IsGone).Sum(p => p.PrivateBytes);
        store.SaveSession(session);
        Log.Write($"Gaming mode ({(automatic ? "auto" : "manual")}): closed [{string.Join(", ", session.ClosedEntryIds)}], " +
                  $"services [{string.Join(", ", session.StoppedServices)}], freed {Format.Bytes(session.Freed)}");
        return session;
    }

    private IReadOnlyList<string> BackToCoding(Settings s, SessionState session, IProgress<string>? progress)
    {
        if (session.StoppedServices.Count > 0)
        {
            progress?.Report("Restarting Windows services...");
            Step("start services", () => Services.RunElevated("start", session.StoppedServices));
        }

        int mySession = MySessionId();
        IReadOnlyList<Detected> Running() => SwitchPlanner.Detect(ProcessSnapshot.Take(), Entries(s), ProcessQuery.GetImagePath,
            ProcessQuery.GetCommandLine, p => p.SessionId != mySession);

        var toOpen = SwitchPlanner.PlanReopen(session, Running());
        var failed = new List<string>();
        var viaShell = new List<ClosedApp>();
        var known = Entries(s).Select(e => e.Id).ToHashSet();
        if (toOpen.Count > 0) progress?.Report($"Reopening {toOpen.Count} app{(toOpen.Count == 1 ? "" : "s")}...");
        foreach (var app in toOpen)
        {
            try
            {
                var start = ReopenTarget.ToStartInfo(app, elevated: Environment.IsPrivilegedProcess);
                using var _ = Process.Start(start);
                // A direct start throws when it fails; one handed to the shell always "succeeds", so check those.
                // (An app whose row was removed while gaming can't be recognised, so it isn't checked.)
                if (start.FileName == "explorer.exe" && known.Contains(app.EntryId)) viaShell.Add(app);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or FileNotFoundException)
            {
                failed.Add(app.Name);
                Log.Write($"Couldn't reopen {app.Name}: {e.Message}");
            }
        }

        // Confirm each shell launch actually produced the app.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var missing = viaShell;
        while (missing.Count > 0)
        {
            missing = SwitchPlanner.PlanReopen(new SessionState { Closed = missing.ToList() }, Running()).ToList();
            if (missing.Count == 0 || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }
        foreach (var app in missing)
        {
            failed.Add(app.Name);
            Log.Write($"Couldn't reopen {app.Name}: it didn't start within 10 s");
        }

        store.ClearSession();
        Log.Write($"Vibe coding mode: reopened {toOpen.Count - failed.Count} of {toOpen.Count}");
        return failed;
    }

    private static void PoliteClose(IEnumerable<ProcInfo> roots, int seconds)
    {
        var procs = roots.Select(Open).OfType<Process>().ToList();
        // Only wait for apps that took the close request; ones that hide to the tray get force-closed straight away.
        var closing = new List<Process>();
        foreach (var p in procs)
        {
            bool accepted = false;
            Step($"close {p.ProcessName}", () => accepted = p.CloseMainWindow());
            if (accepted) closing.Add(p);
        }
        // An app that answered by hiding its window to the tray (still running, no window after a second)
        // isn't going to close; stop waiting for it.
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(seconds);
        bool StillClosing(Process p) =>
            !HasExited(p) && (DateTime.UtcNow - started < TimeSpan.FromSeconds(1) || ProcessQuery.HasVisibleWindow(p.Id));
        while (DateTime.UtcNow < deadline && closing.Any(StillClosing)) Thread.Sleep(250);
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

    private static bool IsGone(ProcInfo info)
    {
        using var p = Open(info);
        return p is null;
    }

    private static int MySessionId()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
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
