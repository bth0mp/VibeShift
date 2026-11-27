using VibeShift.Core.Native;

namespace VibeShift.Core.Processes;

/// <summary>An app's windows: asking all of them to close, and telling whether the app is still there to answer.</summary>
public static class AppWindows
{
    /// <summary>
    /// The windows the process is showing: visible, top-level, owned by no other window (what
    /// Process.MainWindowHandle picks one of), and not cloaked by the app itself. A dialog box is owned by the window
    /// under it, so it isn't listed; a window hidden to the tray isn't visible. A cloaked one is "visible" to Windows
    /// but shown nowhere, which is how PowerToys' Command Palette waits in the background. (A window on another
    /// virtual desktop is cloaked by Windows, not the app: it counts.)
    /// </summary>
    public static IReadOnlyList<IntPtr> Of(int pid) => TopLevel(pid).Where(window => !CloakedByApp(window)).ToList();

    private static List<IntPtr> TopLevel(int pid)
    {
        var found = new List<IntPtr>();
        Win32.EnumWindows((window, _) =>
        {
            if (PidOf(window) == pid && Win32.IsWindowVisible(window) && Win32.GetWindow(window, Win32.GW_OWNER) == IntPtr.Zero) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool CloakedByApp(IntPtr window) =>
        Win32.DwmGetWindowAttribute(window, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && (cloaked & Win32.DWM_CLOAKED_APP) != 0;

    /// <summary>
    /// Asks every window to close, the way its X button does. Process.CloseMainWindow asks one: an app with two
    /// windows kept the other open until it was ended. Cloaked windows are asked too: the app isn't showing them, but
    /// it should still hear the request before it is ended. False when there was no window to ask (or none it's
    /// allowed to: an app running as admin).
    /// </summary>
    public static bool AskToClose(int pid)
    {
        bool asked = false;
        foreach (var window in TopLevel(pid)) asked |= Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return asked;
    }

    /// <summary>
    /// The process shows a window that answers within a second: it is there and listening, not frozen. An app
    /// showing "Save changes?" still answers, since the dialog box keeps its messages moving.
    /// </summary>
    public static bool Responds(int pid) =>
        Of(pid).Any(window =>
            Win32.SendMessageTimeout(window, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero);

    /// <summary>The window in use right now and the process it belongs to; zero when there is none.</summary>
    public static (IntPtr Window, int Pid) InFront()
    {
        var front = Win32.GetForegroundWindow();
        return (front, front == IntPtr.Zero ? 0 : PidOf(front));
    }

    /// <summary>
    /// Brings a window back to the front, restoring it first if it is minimized (a full-screen game minimizes when it
    /// loses the foreground). False when Windows wouldn't allow it. Never waits for the window's own thread: a game
    /// that is busy loading must not hold up the switch.
    /// </summary>
    public static bool PutInFront(IntPtr window)
    {
        if (Win32.IsIconic(window)) Win32.ShowWindowAsync(window, Win32.SW_RESTORE);
        return Win32.SetForegroundWindow(window);
    }

    private static int PidOf(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out int pid);
        return pid;
    }
}

/// <summary>
/// Keeps the window in use in front while apps are asked to close. An app in the background can't normally come
/// forward with its question, but Windows lets it once nobody has touched the PC for a few minutes (a game left
/// loading), and nothing may come up over a game because of a switch. Feed it the window in front, again and again.
/// </summary>
public sealed class FrontGuard(int selfPid, IReadOnlyCollection<int> asked)
{
    // The last window in front that belongs to neither an asked app nor VibeShift. So:
    // - Working in the app that asks (the shortcut pressed there): its question takes over from its own window, and
    //   nothing is remembered to put back. It keeps the keyboard, which is what the user wants just then.
    // - VibeShift's own window in front (Start gaming was clicked there): the same. A question that can come forward should.
    // - Another window brought forward while waiting is the one to put back, not the one in front when it began.
    private IntPtr front;

    /// <summary>Takes the window now in front and its process. Returns the window to put back in front, or zero.</summary>
    public IntPtr Step(IntPtr now, int nowPid)
    {
        if (now == IntPtr.Zero) return IntPtr.Zero;
        if (asked.Contains(nowPid)) return front;
        front = nowPid == selfPid ? IntPtr.Zero : now;
        return IntPtr.Zero;
    }
}
