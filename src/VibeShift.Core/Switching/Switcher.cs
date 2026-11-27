using System.ComponentModel;
using System.Diagnostics;
using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>What switching back opened, and what it couldn't (by app name).</summary>
public sealed record ReopenResult(IReadOnlyList<string> Opened, IReadOnlyList<string> Failed)
{
    /// <summary>
    /// Windows services Extra boost paused that are still stopped: the admin prompt was cancelled, or timed out with
    /// nobody at the PC. The caller keeps them (Settings.PausedServices) so the next switch back tries again.
    /// </summary>
    public IReadOnlyList<string> StillPaused { get; init; } = [];
}

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
            var (_, leftOpen) = CloseNow(all, plan, s.ForceCloseSeconds, s.KeepAppsThatAsk);
            var names = plan.Close.Select(t => t.Entry).Except(leftOpen).Select(e => e.Name).ToList();
            Log.Write($"Vibe page: closed [{string.Join(", ", names)}]");
            LogLeftOpen(leftOpen);
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
        // The game and anything it started: a browser it opened for a login page is the game's, not the browsers row's.
        var gameTree = games.SelectMany(g => ProcessTree.Descendants(all, g)).Select(p => p.Pid).ToHashSet();
        var detected = SwitchPlanner.Detect(all, Entries(s), ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != mySession || gameTree.Contains(p.Pid)
                 || BlockList.IsBlocked(p.Name, ProcessQuery.GetImagePath(p.Pid), p.Pid, self, games));
        var plan = SwitchPlanner.PlanGaming(all, detected, s, automatic, self, (node, root) =>
        {
            if (gameTree.Contains(node.Pid) || node.SessionId != mySession) return true;
            var path = ProcessQuery.GetImagePath(node.Pid);
            return BlockList.IsBlocked(node.Name, path, node.Pid, self, games)
                || SwitchPlanner.IsSeparateApp(path, ProcessQuery.GetImagePath(root.Pid), ProcessQuery.HasVisibleWindow(node.Pid));
        }, ProcessQuery.MyJobPids());
        return (all, plan);
    }

    /// <summary>
    /// Asks politely (where allowed), then ends what's left, parents first. An app that answered with a question is
    /// left open, with everything under it (when <paramref name="keepAsking"/>). Returns what was targeted, and the
    /// rows left open.
    /// </summary>
    private static (List<ProcInfo> Targets, IReadOnlyList<CatalogEntry> LeftOpen) CloseNow(
        IReadOnlyList<ProcInfo> all, GamingPlan plan, int politeSeconds, bool keepAsking)
    {
        if (plan.Close.Count == 0) return ([], []);
        IReadOnlyCollection<int> asking = plan.PoliteCloseRoots.Count > 0 ? PoliteClose(plan.PoliteCloseRoots, politeSeconds, keepAsking) : [];
        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in all) byPid.TryAdd(p.Pid, p);
        var targets = SwitchPlanner.KillOrder(all, plan, asking).Where(byPid.ContainsKey).Select(pid => byPid[pid]).ToList();
        foreach (var info in targets) Kill(info);
        return (targets, SwitchPlanner.LeftOpen(plan, asking));
    }

    private static void LogLeftOpen(IReadOnlyList<CatalogEntry> leftOpen)
    {
        if (leftOpen.Count > 0) Log.Write($"Left open (asked a question): [{string.Join(", ", leftOpen.Select(e => e.Name))}]");
    }

    private static IReadOnlyList<Detected> Running(Settings s)
    {
        int mySession = MySessionId();
        var all = ProcessSnapshot.Take();
        var entries = Entries(s);
        var hidden = SwitchPlanner.BackgroundOnlyTrees(all, entries.SelectMany(e => e.Launchers ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ProcessQuery.GetCommandLine, ProcessQuery.HasVisibleWindow);
        return SwitchPlanner.Detect(all, entries, ProcessQuery.GetImagePath, ProcessQuery.GetCommandLine,
            p => p.SessionId != mySession || hidden.Contains(p.Pid));
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
                if (SwitchPlanner.IsBackgroundOnly(ProcessQuery.GetCommandLine(root.Pid), ProcessQuery.HasVisibleWindow(root.Pid))) continue;
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
        var (targets, leftOpen) = CloseNow(all, plan, s.ForceCloseSeconds, s.KeepAppsThatAsk);
        // Still on the reopen list: answered and closed while gaming, the app comes back with the rest.
        session.LeftOpen.AddRange(leftOpen.Select(e => e.Id));
        LogLeftOpen(leftOpen);

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
                // Record paused services at once, so they're restarted whatever happens next. A failed save must not
                // abort the switch here: the session in memory still has them, and the apps are already closed.
                Step("save session", () => store.SaveSession(session));
            }
        }

        // ponytail: fixed 1 s settle before measuring; poll free RAM until stable if FREED reads low
        Thread.Sleep(1000);
        session.AvailAfter = ProcessQuery.AvailableRamBytes();
        session.KilledBytes = targets.Where(IsGone).Sum(p => p.PrivateBytes);
        Step("save session", () => store.SaveSession(session));
        Log.Write($"Gaming mode ({(automatic ? "auto" : "manual")}): closed [{string.Join(", ", session.ClosedEntryIds.Except(session.LeftOpen))}], " +
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

        // What was closed for gaming, plus the user's vibe apps that aren't open yet.
        var toOpen = SwitchPlanner.PlanVibeLaunch(session, Running(s), s.VibeApps, LaunchTargets(s));
        var result = Launch(s, toOpen, progress);

        // After the apps: restarting services waits on a UAC prompt, and a switch back after the game closes may
        // happen with nobody at the PC. session.json still lists the services until the end, so a crash here
        // restarts them next time.
        var paused = RestartServices(session.StoppedServices.Concat(s.PausedServices), progress);
        Step("save stats", () => store.AddStat(new GamingRecord(session.StartedUtc, DateTime.UtcNow, session.Freed), DateTime.UtcNow));
        store.ClearSession();
        Log.Write($"Vibe coding mode: opened {result.Opened.Count} of {toOpen.Count}");
        return result with { StillPaused = paused };
    }

    /// <summary>Settings: "Restart them now" for services an earlier switch back left paused. Returns those still stopped.</summary>
    public Task<IReadOnlyList<string>> RestartServicesAsync(IReadOnlyCollection<string> names) =>
        Task.Run(() => RestartServices(names, null));

    /// <summary>
    /// Starts the paused services that aren't running (one admin prompt, none when they all already run, e.g. after a
    /// reboot). Returns the ones still stopped: the prompt was cancelled, or timed out with nobody at the PC.
    /// </summary>
    private static IReadOnlyList<string> RestartServices(IEnumerable<string> names, IProgress<string>? progress)
    {
        var stopped = names.Where(Services.IsAllowed).Distinct(StringComparer.OrdinalIgnoreCase).Where(Services.IsStopped).ToList();
        if (stopped.Count == 0) return [];
        progress?.Report("Restarting Windows services...");
        Step("start services", () => Services.RunElevated("start", stopped));
        var still = stopped.Where(Services.IsStopped).ToList();
        if (still.Count > 0) Log.Write($"Windows services still paused: [{string.Join(", ", still)}]");
        return still;
    }

    /// <summary>Starts each app and confirms the ones handed to the shell (Store apps) really appear.</summary>
    private static ReopenResult Launch(Settings s, IReadOnlyList<ClosedApp> toOpen, IProgress<string>? progress)
    {
        var failed = new List<string>();
        var viaShell = new List<ClosedApp>();
        var known = Entries(s).Select(e => e.Id).ToHashSet();
        if (toOpen.Count > 0) progress?.Report($"Reopening {toOpen.Count} app{(toOpen.Count == 1 ? "" : "s")}...");
        // Inside a job (started by Task Scheduler, or with --tray from an AI app's terminal), an app started directly
        // would join that job and end with it: when the task is stopped, or that AI app closes. Explorer starts it
        // outside, the way it keeps an elevated VibeShift's admin rights out of the apps it reopens.
        bool viaExplorer = Environment.IsPrivilegedProcess || ProcessQuery.MyJobPids().Count > 0;
        foreach (var app in toOpen)
        {
            try
            {
                var start = ReopenTarget.ToStartInfo(app, viaExplorer);
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

        // Confirm each shell launch actually produced the app. Timed by uptime, not the clock: with the clock set back
        // mid-switch, a wait like this one ran on until the clock caught up, the switch never finishing.
        long deadline = Environment.TickCount64 + 10_000;
        var missing = viaShell;
        while (missing.Count > 0)
        {
            missing = SwitchPlanner.PlanReopen(new SessionState { Closed = missing.ToList() }, Running(s)).ToList();
            if (missing.Count == 0 || Environment.TickCount64 >= deadline) break;
            Thread.Sleep(500);
        }
        foreach (var app in missing)
        {
            failed.Add(app.Name);
            Log.Write($"Couldn't reopen {app.Name}: it didn't start within 10 s");
        }
        return new ReopenResult(toOpen.Select(a => a.Name).Where(n => !failed.Contains(n)).Distinct().ToList(), failed);
    }

    /// <summary>
    /// Asks each app to close and gives them <paramref name="seconds"/>. Returns the roots that asked a question
    /// instead (when <paramref name="keepAsking"/>): the caller leaves those open.
    /// </summary>
    private static IReadOnlyCollection<int> PoliteClose(IEnumerable<ProcInfo> roots, int seconds, bool keepAsking)
    {
        var procs = roots.Select(Open).OfType<Process>().ToList();
        // Only wait for apps that took the close request; ones with no window to ask get force-closed straight away.
        var closing = new List<Process>();
        foreach (var p in procs)
        {
            bool accepted = false;
            // Every window, not just the main one: a second window still open would read as "it's asking" below.
            Step($"close {p.ProcessName}", () => accepted = AppWindows.AskToClose(p.Id));
            if (accepted) closing.Add(p);
        }
        // An app that answered by hiding its window to the tray (still running, no window after a second)
        // isn't going to close; stop waiting for it.
        long started = Environment.TickCount64;
        long deadline = started + seconds * 1000L;
        bool StillClosing(Process p) =>
            !HasExited(p) && (Environment.TickCount64 - started < 1000 || ProcessQuery.HasVisibleWindow(p.Id));
        while (Environment.TickCount64 < deadline && closing.Any(StillClosing)) Thread.Sleep(250);
        // Asked to close, still running, still showing a window that answers: it didn't hide to the tray and it isn't
        // frozen. It put up a question ("Save changes?"), or it's busy saving. Ending it here is how unsaved work was
        // lost, so it stays, and the user is told.
        // ponytail: an app that minimizes instead of closing reads as asking too; the setting is the way out.
        var asking = keepAsking ? closing.Where(p => !HasExited(p) && AppWindows.Responds(p.Id)).Select(p => p.Id).ToList() : [];
        foreach (var p in procs) p.Dispose();
        return asking;
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
