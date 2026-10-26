namespace VibeShift.Core.Processes;

/// <summary>
/// Parent/child queries over a snapshot. A parent only counts if it was created no later than
/// the child: Windows reuses PIDs, so a recorded parent PID may now belong to a newer process.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// The root and everything under it, children before parents, each process once.
    /// A non-root process for which <paramref name="skip"/> is true is left out together with everything under it.
    /// </summary>
    public static IReadOnlyList<ProcInfo> Descendants(IReadOnlyList<ProcInfo> all, int rootPid, Func<ProcInfo, bool>? skip = null)
    {
        var root = all.FirstOrDefault(p => p.Pid == rootPid);
        if (root is null) return [];
        var children = ChildMap(all);
        var result = new List<ProcInfo>();
        var seen = new HashSet<int>();
        Visit(root);
        return result;

        void Visit(ProcInfo p)
        {
            if (!seen.Add(p.Pid)) return;
            if (children.TryGetValue(p.Pid, out var kids))
                foreach (var k in kids)
                    if (skip is null || !skip(k)) Visit(k);
            result.Add(p);
        }
    }

    /// <summary>The matched processes whose valid parent is not itself matched.</summary>
    public static IReadOnlyList<ProcInfo> Roots(IReadOnlyList<ProcInfo> all, IReadOnlyCollection<ProcInfo> matched)
    {
        var byPid = ByPid(all);
        var matchedPids = matched.Select(m => m.Pid).ToHashSet();
        return matched.Where(m => Parent(m, byPid) is not { } parent || !matchedPids.Contains(parent.Pid)).ToList();
    }

    /// <summary>The PID itself plus every valid ancestor.</summary>
    public static IReadOnlySet<int> Ancestors(IReadOnlyList<ProcInfo> all, int pid)
    {
        var byPid = ByPid(all);
        var chain = new HashSet<int> { pid };
        var current = byPid.GetValueOrDefault(pid);
        while (current is not null && Parent(current, byPid) is { } parent && chain.Add(parent.Pid))
            current = parent;
        return chain;
    }

    private static Dictionary<int, ProcInfo> ByPid(IReadOnlyList<ProcInfo> all)
    {
        var map = new Dictionary<int, ProcInfo>(all.Count);
        foreach (var p in all) map.TryAdd(p.Pid, p);
        return map;
    }

    private static Dictionary<int, List<ProcInfo>> ChildMap(IReadOnlyList<ProcInfo> all)
    {
        var byPid = ByPid(all);
        var children = new Dictionary<int, List<ProcInfo>>();
        foreach (var p in all)
        {
            if (Parent(p, byPid) is not { } parent) continue;
            if (!children.TryGetValue(parent.Pid, out var list)) children[parent.Pid] = list = [];
            list.Add(p);
        }
        return children;
    }

    private static ProcInfo? Parent(ProcInfo child, Dictionary<int, ProcInfo> byPid) =>
        byPid.TryGetValue(child.ParentPid, out var parent) && parent.Pid != child.Pid && parent.CreateTime <= child.CreateTime
            ? parent
            : null;
}
