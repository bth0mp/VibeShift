using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>
/// A catalog row that is running: its root processes, everything under them, their RAM, and the
/// processes that matched the row itself (Members can include other apps' processes it started).
/// </summary>
public sealed record Detected(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<ProcInfo> Members, long Bytes, IReadOnlyList<ProcInfo> Matched);

public sealed record CloseTarget(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<int> KillPids, IReadOnlyList<ProcInfo> ReopenRoots);

public sealed record GamingPlan(
    IReadOnlyList<CloseTarget> Close,
    bool ShutdownWsl,
    bool PoliteClose,
    IReadOnlyList<string> StopServices,
    long EstimatedBytes)
{
    /// <summary>Every row this switch closes, including Docker / WSL when only WSL is shut down.</summary>
    public IReadOnlyList<string> ClosedEntryIds { get; init; } = [];
}

/// <summary>Every switching decision, as pure functions over a snapshot. The Switcher only carries them out.</summary>
public static class SwitchPlanner
{
    public static IReadOnlyList<Detected> Detect(
        IReadOnlyList<ProcInfo> all,
        IReadOnlyList<CatalogEntry> entries,
        Func<int, string?> imagePath,
        Func<int, string?> commandLine,
        Func<ProcInfo, bool> blocked)
    {
        var matched = entries.ToDictionary(e => e.Id, _ => new List<ProcInfo>());
        foreach (var p in all)
        {
            var e = AppCatalog.Match(p, entries, imagePath, commandLine);
            if (e is not null && !blocked(p)) matched[e.Id].Add(p);
        }
        var vm = WslVm(all);

        var result = new List<Detected>();
        foreach (var e in entries)
        {
            var procs = matched[e.Id];
            bool dockerWsl = e.Kind == EntryKind.DockerWsl;
            if (procs.Count == 0 && !(dockerWsl && vm.Count > 0)) continue;

            var roots = ProcessTree.Roots(all, procs);
            var members = roots.SelectMany(r => ProcessTree.Descendants(all, r.Pid))
                .Concat(dockerWsl ? vm : [])
                .DistinctBy(p => p.Pid)
                .ToList();
            result.Add(new Detected(e, roots, members, members.Sum(m => m.PrivateBytes), procs));
        }
        return result;
    }

    /// <param name="spare">
    /// (process, root it was found under) -> true to keep that process and everything under it alive:
    /// the game, Windows' own processes, separate apps the target merely launched.
    /// </param>
    public static GamingPlan PlanGaming(
        IReadOnlyList<ProcInfo> all,
        IReadOnlyList<Detected> detected,
        Settings s,
        bool automatic,
        int selfPid,
        Func<ProcInfo, ProcInfo, bool> spare)
    {
        var selfChain = ProcessTree.Ancestors(all, selfPid);
        // An app whose row is off survives even when a switched-on app started it.
        var offRoots = detected.Where(d => !s.IsOn(d.Entry)).SelectMany(d => d.Roots).Select(r => r.Pid).ToHashSet();
        var close = new List<CloseTarget>();
        foreach (var d in detected)
        {
            if (!s.IsOn(d.Entry) || d.Roots.Count == 0) continue;
            // VibeShift runs inside this app (e.g. started from its terminal): closing part of it would leave
            // a broken app we couldn't reopen, so leave the whole app alone.
            if (d.Roots.Any(r => selfChain.Contains(r.Pid))) continue;
            // Parents first: a supervisor that's already dead can't respawn the children killed after it.
            var kill = d.Roots
                .SelectMany(r => ProcessTree.Descendants(all, r.Pid, node => offRoots.Contains(node.Pid) || spare(node, r)).Reverse())
                .Select(p => p.Pid)
                .Distinct()
                .ToList();
            // Reopen the launcher exe, even when it runs under one of its own helpers (Docker Desktop under its backend).
            string? launcher = d.Entry.ProcessNames.FirstOrDefault();
            var reopen = d.Entry.Reopen && launcher is not null
                ? ProcessTree.Roots(all, d.Matched.Where(m => m.Name.Equals(launcher, StringComparison.OrdinalIgnoreCase)).ToList())
                : [];
            close.Add(new CloseTarget(d.Entry, d.Roots, kill, reopen));
        }

        var vm = WslVm(all);
        var dockerWsl = detected.FirstOrDefault(d => d.Entry.Kind == EntryKind.DockerWsl);
        bool shutdownWsl = dockerWsl is not null && s.IsOn(dockerWsl.Entry) && vm.Count > 0;

        // Each process frees its RAM once, however many rows it belongs to.
        var byPid = new Dictionary<int, ProcInfo>();
        foreach (var p in all) byPid.TryAdd(p.Pid, p);
        var killed = close.SelectMany(t => t.KillPids).ToHashSet();
        long estimated = killed.Sum(pid => byPid[pid].PrivateBytes)
            + (shutdownWsl ? vm.Where(v => !killed.Contains(v.Pid)).Sum(v => v.PrivateBytes) : 0);

        var closedIds = close.Select(t => t.Entry.Id).ToList();
        if (shutdownWsl && !closedIds.Contains(dockerWsl!.Entry.Id)) closedIds.Add(dockerWsl.Entry.Id);

        return new GamingPlan(
            close,
            shutdownWsl,
            PoliteClose: !automatic,
            StopServices: s.ExtraBoost && !automatic ? s.BoostServices.ToList() : [],
            estimated)
        {
            ClosedEntryIds = closedIds,
        };
    }

    /// <summary>What to reopen, in closing order, minus apps that are already running again.</summary>
    public static IReadOnlyList<ClosedApp> PlanReopen(SessionState session, IReadOnlyList<Detected> runningNow)
    {
        var running = runningNow.Where(d => d.Roots.Count > 0).Select(d => d.Entry.Id).ToHashSet();
        return session.Closed.Where(c => !running.Contains(c.EntryId)).ToList();
    }

    /// <summary>
    /// A windowed program installed outside the target app's folder is a separate app the target merely
    /// launched (a browser opened from a link), not part of the target.
    /// </summary>
    public static bool IsSeparateApp(string? processPath, string? rootPath, bool hasVisibleWindow)
    {
        if (!hasVisibleWindow || processPath is null || rootPath is null) return false;
        var rootDir = Path.GetDirectoryName(rootPath);
        return rootDir is null || !processPath.StartsWith(rootDir.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The WSL VM process: "vmmemWSL", or plain "vmmem" on older Windows 10 WSL. Hyper-V VMs are also
    /// "vmmem", so that name only counts while WSL itself is running (wslhost present).
    /// </summary>
    private static List<ProcInfo> WslVm(IReadOnlyList<ProcInfo> all)
    {
        bool wslRunning = all.Any(p => p.Name.Equals("wslhost", StringComparison.OrdinalIgnoreCase));
        return all.Where(p => p.Name.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase)
                              || (wslRunning && p.Name.Equals("vmmem", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
