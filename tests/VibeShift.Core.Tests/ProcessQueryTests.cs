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
        Assert.Equal(me.SessionId, p.SessionId); // M14: proves the SessionId offset
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

    [Fact] // a process with no window, and one that's gone: nothing to ask, and nothing asking (the rest is a live check)
    public void A_process_without_windows_is_not_asking()
    {
        Assert.Empty(AppWindows.Of(Environment.ProcessId));
        Assert.False(AppWindows.AskToClose(Environment.ProcessId));
        Assert.False(AppWindows.Responds(Environment.ProcessId));
        Assert.Empty(AppWindows.Of(0x7FFFFFF0));
        Assert.False(AppWindows.Responds(0x7FFFFFF0));
    }

    // The window in front while apps are asked to close: 0x10 a game (process 7), 0x20 a question from an app that
    // was asked (process 50), VibeShift itself is process 1.
    private static FrontGuard Guard() => new(selfPid: 1, asked: [50]);

    [Fact]
    public void A_question_that_comes_to_the_front_gives_way_to_the_window_that_was_there()
    {
        var guard = Guard();
        Assert.Equal(0, guard.Step(0x10, 7));
        Assert.Equal(0, guard.Step(0x10, 7));
        Assert.Equal(0x10, guard.Step(0x20, 50));
    }

    [Fact] // 2026-11-27 review: pressing the shortcut from the app that asks took the keyboard off its own question
    public void The_app_you_were_working_in_keeps_its_question_in_front()
    {
        var guard = Guard();
        Assert.Equal(0, guard.Step(0x30, 50)); // its main window was in front
        Assert.Equal(0, guard.Step(0x20, 50)); // now its question is: leave it
    }

    [Fact]
    public void With_VibeShifts_own_window_in_front_the_question_may_come_forward()
    {
        var guard = Guard();
        Assert.Equal(0, guard.Step(0x40, 1));
        Assert.Equal(0, guard.Step(0x20, 50));
        // ...also when VibeShift's window was opened after the switch began
        guard = Guard();
        Assert.Equal(0, guard.Step(0x10, 7));
        Assert.Equal(0, guard.Step(0x40, 1));
        Assert.Equal(0, guard.Step(0x20, 50));
    }

    [Fact] // 2026-11-27 review: the window put back was the one in front when the switch began, not the one in use since
    public void A_window_brought_forward_meanwhile_is_the_one_put_back()
    {
        var guard = Guard();
        Assert.Equal(0, guard.Step(0x10, 7));
        Assert.Equal(0, guard.Step(0x11, 8));
        Assert.Equal(0x11, guard.Step(0x20, 50));
        Assert.Equal(0, guard.Step(0x11, 8)); // back in front: nothing to do
        Assert.Equal(0x11, guard.Step(0x20, 50)); // and again, should the question come forward once more
    }

    [Fact]
    public void With_nothing_in_front_there_is_nothing_to_put_back()
    {
        var guard = Guard();
        Assert.Equal(0, guard.Step(0, 0));
        Assert.Equal(0, guard.Step(0x20, 50));
    }

    [Fact]
    public void Ram_is_sane() => Assert.InRange(ProcessQuery.AvailableRamBytes(), 1, ProcessQuery.TotalRamBytes());

    [Fact] // when this sign-in began = the oldest process in the logon session; other sessions and odd entries don't count
    public void Sign_in_time_is_the_oldest_process_in_the_session()
    {
        var t = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        ProcInfo P(int pid, DateTime created, int session) => new(pid, 0, "p", 0, created.ToFileTimeUtc(), session);
        var all = new[] { P(1, t.AddDays(-3), 0), P(2, t.AddMinutes(5), 1), P(3, t, 1), new ProcInfo(4, 0, "odd", 0, 0, 1) };
        Assert.Equal(t, ProcessQuery.SignInTimeUtc(all, sessionId: 1));
        Assert.Null(ProcessQuery.SignInTimeUtc(all, sessionId: 9));
    }

    [Fact] // the real thing: this test's own session began before this test process did
    public void Sign_in_time_of_this_session_is_before_this_process()
    {
        using var me = Process.GetCurrentProcess();
        var signIn = ProcessQuery.SignInTimeUtc(ProcessSnapshot.Take(), me.SessionId);
        Assert.NotNull(signIn);
        Assert.True(signIn <= me.StartTime.ToUniversalTime());
    }
}
