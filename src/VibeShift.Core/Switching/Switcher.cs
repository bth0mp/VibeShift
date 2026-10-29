using System.ComponentModel;
using System.Diagnostics;
using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>What switching back opened, and what it couldn't (by app name).</summary>
public sealed record ReopenResult(IReadOnlyList<string> Opened, IReadOnlyList<string> Failed);

/// <summary>
/// Carries out SwitchPlanner's decisions. Every step and every process is attempted on its own:
/// one failure is logged and skipped, never allowed to stop the rest.
/// </summary>
public sealed class Switcher(JsonStore store)
{
    /// <summary>The user's own apps first, so a custom exe that shares a name with a built-in wins.</summary>
    public static IReadOnlyList<CatalogEntry> Entries(Settings s) =>
        s.CustomApps.Select(AppCatalog.Custom).Concat(AppCatalog.BuiltIn).DistinctBy(e => e.Id).ToList();

    /// <summary>
    /// How to start every app the user could pick as a vibe app: launchers recorded while it ran (which know a
    /// Store app's AUMID), else a custom app's own exe path.
    /// </summary>
    public static IReadOnlyDictionary<string, ClosedApp> LaunchTargets(Settings s)
    {
        var targets = new Dictionary<string, ClosedApp>(s.LaunchTargets);
        foreach (var path in s.CustomApps)
        {
            var e = AppCatalog.Custom(path);
            targets.TryAdd(e.Id, new ClosedApp(e.Id, e.Name, path, null));
        }
        return targets;
    }

    public Task<SessionState> StartGamingAsync(Settings s, bool automatic, IReadOnlyCollection<int> gamePids, IProgress<string>? progress) =>
        Task.Run(() => StartGaming(s, automatic, gamePids, progress));

    public Task<ReopenResult> BackToCodingAsync(Settings s, SessionState session, IProgress<string>? progress) =>
        Task.Run(() => BackToCoding(s, session, progress));

    /// <summary>
    /// Vibe page: close just these apps now, with the same care as a switch (polite first, never the game,
    /// Windows or separate apps). No mode change, no MCP, WSL or services. Returns the names closed.
    /// </summary>
    public Task<IReadOnlyList<string>> CloseAppsAsync(Settings s, IReadOnlyCollection<string> ids, IReadOnlyCollection<int> gamePids) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var (all, plan) = PlanClosing(s.ForClosing(ids), automatic: false, gamePids);
            CloseNow(all, plan, s.ForceCloseSeconds);
            var names = plan.Close.Select(t => t.Entry.Name).ToList();
            Log.Write($"Vibe page: closed [{string.Join(", ", names)}]");
            return names;
        });

    /// <summary>Vibe page: open these apps now (those not already running), confirming Store apps really start.</summary>
    public Task<ReopenResult> OpenAppsAsync(Settings s, IReadOnlyCollection<string> ids) =>
        Task.Run(() =>
        {
            var toOpen = SwitchPlanner.PlanVibeLaunch(new SessionState(), Running(s), ids, LaunchTargets(s));
            var result = Launch(s, toOpen, null);
            Log.Write($"Vibe page: opened [{string.Join(", ", result.Opened)}]" +
                      (result.Failed.Count > 0 ? $", couldn't open [{string.Join(", ", result.Failed)}]" : ""));
            return result;
        });

    /// <summary>Snapshot, detect and plan a close, never reaching the game (or anything it started), Windows'
    /// own processes, other users' processes or separate windowed apps.</summary>
    private static (IReadOnlyList<ProcInfo> All, GamingPlan Plan) PlanClosing(Settings s, bool automatic, IReadOnlyCollection<int> gamePids)
    {
        var all = ProcessSnapshot.Take();
        int self = Environment.ProcessId;
        int mySession = MySessionId();
        var games = gamePids.ToHashSet();
        var detected = SwitchPlanner.Detect(all, Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != mySession || BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, games));
        var gameTree = games.SelectMany(g => ProcessTree.Descendants(all, g)).Select(p => p.Pid).ToHashSet();
        var plan = SwitchPlanner.PlanGaming(all, detected, s, automatic, self, (node, root) =>
        {
            if (gameTree.Contains(node.Pid) || node.SessionId != mySession) return true;
            var path = ProcessQuery.GetImagePath(node.Pid);
            return BlockList.IsBlocked(node.Name, path, node.Pid, self, games)
                || SwitchPlanner.IsSeparateApp(path, ProcessQuery.GetImagePath(root.Pid), ProcessQuery.HasVisibleWindow(node.Pid));
        });
        return (all, plan);
    }

    /// <summary>Asks politely (where allowed), then ends what's left, parents first. Returns what was targeted.</summary>
    private static List<ProcInfo> CloseNow(IReadOnlyList<ProcInfo> all, GamingPlan plan, int politeSeconds)
    {
        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in all) byPid.TryAdd(p.Pid, p);
        var targets = SwitchPlanner.KillOrder(all, plan).Where(byPid.ContainsKey).Select(pid => byPid[pid]).ToList();
        if (plan.Close.Count == 0) return targets;
        if (plan.PoliteCloseRoots.Count > 0) PoliteClose(plan.PoliteCloseRoots, politeSeconds);
        foreach (var info in targets) Kill(info);
        return targets;
    }

    private static IReadOnlyList<Detected> Running(Settings s)
    {
        int mySession = MySessionId();
        return SwitchPlanner.Detect(ProcessSnapshot.Take(), Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != mySession);
    }

    private SessionState StartGaming(Settings s, bool automatic, IReadOnlyCollection<int> gamePids, IProgress<string>? progress)
    {
        var session = new SessionState { Automatic = automatic, AvailBefore = ProcessQuery.AvailableRamBytes() };
        var (all, plan) = PlanClosing(s, automatic, gamePids);

        session.ClosedEntryIds.AddRange(plan.ClosedEntryIds);
        foreach (var target in plan.Close)
        {
            foreach (var root in target.ReopenRoots)
            {
                // A browser only running in the background (Edge startup boost) had no window to bring back.
                if (ProcessQuery.GetCommandLine(root.Pid)?.Contains("--no-startup-window", StringComparison.OrdinalIgnoreCase) == true) continue;
                var exe = ProcessQuery.GetImagePath(root.Pid) is { } path ? ReopenTarget.PreferStub(path, File.Exists) : null;
                var app = new ClosedApp(target.Entry.Id, target.Entry.Name, exe, ProcessQuery.GetAumid(root.Pid));
                if ((app.ExePath ?? app.Aumid) is not null && !session.Closed.Contains(app)) session.Closed.Add(app);
            }
        }
        session.WslShutDown = plan.ShutdownWsl;
        // Gaming power plan: the current plan goes in the session first, so even a crash can put it back.
        if (s.GamingPowerPlan is { } gamingPlan)
            Step("read power plan", () =>
            {
                if (PowerPlans.List().FirstOrDefault(p => p.Active) is { } active && active.Guid != gamingPlan) session.PowerPlanBefore = active.Guid;
            });
        store.SaveSession(session); // before anything is closed, so a crash mid-switch can still switch back
        if (session.PowerPlanBefore is not null) Step("set power plan", () => PowerPlans.SetActive(s.GamingPowerPlan!));

        if (plan.Close.Count > 0)
        {
            int n = plan.Close.Count;
            progress?.Report($"Closing {n} app{(n == 1 ? "" : "s")}...");
        }
        var targets = CloseNow(all, plan, s.ForceCloseSeconds);

        if (plan.ShutdownWsl)
        {
            progress?.Report("Shutting down WSL...");
            // By full path: a bare name is searched for in the current folder (a terminal's, say) before System32.
            Step("wsl --shutdown", () => RunHidden(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown"));
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

        // Game-time apps (Discord, OBS...) open after the RAM was measured, so FREED shows what closing gained.
        var gameApps = SwitchPlanner.PlanVibeLaunch(new SessionState(), Running(s), s.GameApps, LaunchTargets(s));
        if (gameApps.Count > 0)
        {
            progress?.Report("Opening your game apps...");
            var opened = Launch(s, gameApps, null);
            Log.Write($"Game apps: opened [{string.Join(", ", opened.Opened)}]");
        }
        return session;
    }

    private ReopenResult BackToCoding(Settings s, SessionState session, IProgress<string>? progress)
    {
        if (session.PowerPlanBefore is { } before) Step("restore power plan", () => PowerPlans.SetActive(before));
        if (session.StoppedServices.Count > 0)
        {
            progress?.Report("Restarting Windows services...");
            Step("start services", () => Services.RunElevated("start", session.StoppedServices));
        }

        // What was closed for gaming, plus the user's vibe apps that aren't open yet.
        var toOpen = SwitchPlanner.PlanVibeLaunch(session, Running(s), s.VibeApps, LaunchTargets(s));
        var result = Launch(s, toOpen, progress);
        Step("save stats", () => store.AddStat(new GamingRecord(session.StartedUtc, DateTime.UtcNow, session.Freed), DateTime.UtcNow));
        store.ClearSession();
        Log.Write($"Vibe coding mode: opened {result.Opened.Count} of {toOpen.Count}");
        return result;
    }

    /// <summary>Starts each app and confirms the ones handed to the shell (Store apps) really appear.</summary>
    private static ReopenResult Launch(Settings s, IReadOnlyList<ClosedApp> toOpen, IProgress<string>? progress)
    {
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
                if (start.FileName == ReopenTarget.Explorer && known.Contains(app.EntryId)) viaShell.Add(app);
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
            missing = SwitchPlanner.PlanReopen(new SessionState { Closed = missing.ToList() }, Running(s)).ToList();
            if (missing.Count == 0 || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }
        foreach (var app in missing)
        {
            failed.Add(app.Name);
            Log.Write($"Couldn't reopen {app.Name}: it didn't start within 10 s");
        }
        return new ReopenResult(toOpen.Select(a => a.Name).Where(n => !failed.Contains(n)).Distinct().ToList(), failed);
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
