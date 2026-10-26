using VibeShift.Core.Processes;

namespace VibeShift.Core.Tests;

public class ProcessTreeTests
{
    private static ProcInfo P(int pid, int ppid, string name, long ct = 1, long bytes = 10) => new(pid, ppid, name, bytes, ct);

    [Fact]
    public void Descendants_lists_children_before_root_once()
    {
        var all = new[] { P(1, 0, "a"), P(2, 1, "b"), P(3, 2, "c"), P(4, 1, "d") };
        var d = ProcessTree.Descendants(all, 1);
        Assert.Equal(4, d.Count);
        Assert.Equal(1, d[^1].Pid);
        Assert.True(d.ToList().FindIndex(x => x.Pid == 3) < d.ToList().FindIndex(x => x.Pid == 2));
    }

    [Fact] // Review Focus 3
    public void Child_is_not_attached_to_a_parent_pid_reused_later()
    {
        var all = new[] { P(10, 0, "new", ct: 500), P(20, 10, "old", ct: 100) };
        Assert.Single(ProcessTree.Descendants(all, 10));
        Assert.Equal(new[] { 20 }, ProcessTree.Ancestors(all, 20).ToArray());
    }

    [Fact]
    public void Electron_helpers_collapse_to_one_root_and_two_instances_give_two()
    {
        var all = new[] { P(1, 0, "Cursor"), P(2, 1, "Cursor"), P(3, 1, "Cursor"), P(9, 0, "Cursor") };
        Assert.Equal(new[] { 1, 9 }, ProcessTree.Roots(all, all).Select(r => r.Pid).OrderBy(x => x));
    }
}
