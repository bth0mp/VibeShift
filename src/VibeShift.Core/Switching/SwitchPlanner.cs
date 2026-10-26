using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>A catalog row that is running: its root processes, everything under them, and their RAM.</summary>
public sealed record Detected(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<ProcInfo> Members, long Bytes);

public sealed record CloseTarget(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<int> KillPids, IReadOnlyList<ProcInfo> ReopenRoots);

public sealed record GamingPlan(
    IReadOnlyList<CloseTarget> Close,
    bool ShutdownWsl,
    bool PoliteClose,
    IReadOnlyList<string> StopServices,
    long EstimatedBytes);

/// <summary>Every switching decision, as pure functions over a snapshot. The Switcher only carries them out.</summary>
public static class SwitchPlanner
{
    private const string WslVm = "vmmemWSL";

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
        var vm = all.Where(IsWslVm).ToList();

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
            result.Add(new Detected(e, roots, members, members.Sum(m => m.PrivateBytes)));
        }
        return result;
    }

    public static GamingPlan PlanGaming(IReadOnlyList<ProcInfo> all, IReadOnlyList<Detected> detected, Settings s, bool automatic, int selfPid)
    {
        var spare = ProcessTree.Ancestors(all, selfPid);
        var close = new List<CloseTarget>();
        foreach (var d in detected)
        {
            if (!s.IsOn(d.Entry) || d.Roots.Count == 0) continue;
            var kill = d.Roots
                .SelectMany(r => ProcessTree.Descendants(all, r.Pid))
                .Select(p => p.Pid)
                .Where(pid => !spare.Contains(pid))
                .Distinct()
                .ToList();
            var reopen = d.Entry.Reopen
                ? d.Roots.Where(r => r.Name.Equals(d.Entry.ProcessNames[0], StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            close.Add(new CloseTarget(d.Entry, d.Roots, kill, reopen));
        }

        bool shutdownWsl = detected.Any(d => d.Entry.Kind == EntryKind.DockerWsl && s.IsOn(d.Entry)) && all.Any(IsWslVm);
        long estimated = detected
            .Where(d => s.IsOn(d.Entry) && (d.Roots.Count > 0 || (d.Entry.Kind == EntryKind.DockerWsl && shutdownWsl)))
            .Sum(d => d.Bytes);

        return new GamingPlan(
            close,
            shutdownWsl,
            PoliteClose: !automatic,
            StopServices: s.ExtraBoost && !automatic ? s.BoostServices.ToList() : [],
            estimated);
    }

    /// <summary>What to reopen, in closing order, minus apps that are already running again.</summary>
    public static IReadOnlyList<ClosedApp> PlanReopen(SessionState session, IReadOnlyList<Detected> runningNow)
    {
        var running = runningNow.Where(d => d.Roots.Count > 0).Select(d => d.Entry.Id).ToHashSet();
        return session.Closed.Where(c => !running.Contains(c.EntryId)).ToList();
    }

    private static bool IsWslVm(ProcInfo p) => p.Name.Equals(WslVm, StringComparison.OrdinalIgnoreCase);
}
