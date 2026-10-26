using VibeShift.Core.Processes;

namespace VibeShift.Core.Tests;

public class ProcessQueryTests
{
    [Fact]
    public void Snapshot_has_current_process_with_correct_fields()
    {
        using var me = Process.GetCurrentProcess();
        var p = ProcessSnapshot.Take().Single(x => x.Pid == me.Id);
        Assert.Equal(me.ProcessName, p.Name, ignoreCase: true);
        Assert.Equal(me.StartTime.ToFileTimeUtc(), p.CreateTime); // proves struct offsets
        Assert.True(p.PrivateBytes > 0);
        Assert.True(p.ParentPid > 0);
    }

    [Fact]
    public void Image_path_and_command_line_are_readable_for_self()
    {
        Assert.Equal(Environment.ProcessPath, ProcessQuery.GetImagePath(Environment.ProcessId), ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(ProcessQuery.GetCommandLine(Environment.ProcessId)));
    }

    [Fact]
    public void Unpackaged_self_has_no_aumid() => Assert.Null(ProcessQuery.GetAumid(Environment.ProcessId));

    [Fact]
    public void Dead_pid_gives_nulls()
    {
        Assert.Null(ProcessQuery.GetImagePath(0x7FFFFFF0));
        Assert.Null(ProcessQuery.GetCommandLine(0x7FFFFFF0));
    }

    [Fact]
    public void Ram_is_sane() => Assert.InRange(ProcessQuery.AvailableRamBytes(), 1, ProcessQuery.TotalRamBytes());
}
