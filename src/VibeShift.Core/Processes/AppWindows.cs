using VibeShift.Core.Native;

namespace VibeShift.Core.Processes;

/// <summary>An app's windows: asking all of them to close, and telling whether the app is still there to answer.</summary>
public static class AppWindows
{
    /// <summary>
    /// The process's visible top-level windows that no other window owns: what Process.MainWindowHandle picks one of.
    /// A dialog box is owned by the window under it, so it isn't listed; a window hidden to the tray isn't visible.
    /// Nor is one the app has cloaked: "visible" to Windows but shown nowhere, which is how PowerToys' Command Palette
    /// waits in the background. (A window on another virtual desktop is cloaked by Windows, not the app: it counts.)
    /// </summary>
    public static IReadOnlyList<IntPtr> Of(int pid)
    {
        var found = new List<IntPtr>();
        Win32.EnumWindows((window, _) =>
        {
            Win32.GetWindowThreadProcessId(window, out int owner);
            if (owner == pid && Win32.IsWindowVisible(window) && Win32.GetWindow(window, Win32.GW_OWNER) == IntPtr.Zero
                && !CloakedByApp(window)) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool CloakedByApp(IntPtr window) =>
        Win32.DwmGetWindowAttribute(window, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && (cloaked & Win32.DWM_CLOAKED_APP) != 0;

    /// <summary>
    /// Asks every window to close, the way its X button does. Process.CloseMainWindow asks one: an app with two
    /// windows kept the other open until it was ended. False when there was no window to ask (or none it's allowed
    /// to: an app running as admin).
    /// </summary>
    public static bool AskToClose(int pid)
    {
        bool asked = false;
        foreach (var window in Of(pid)) asked |= Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return asked;
    }

    /// <summary>The window in use right now, or zero when it is this process's own (or there is none).</summary>
    public static IntPtr InFrontAndNotOurs()
    {
        var front = Win32.GetForegroundWindow();
        return front != IntPtr.Zero && PidOf(front) != Environment.ProcessId ? front : IntPtr.Zero;
    }

    /// <summary>
    /// Puts <paramref name="front"/> (from <see cref="InFrontAndNotOurs"/>) back in front if a window of one of
    /// <paramref name="pids"/> has taken its place: an app asked to close that came forward with its question. Windows
    /// stops an app in the background doing that while the PC is in use, but not once nobody has touched it for a few
    /// minutes (a game left loading), and nothing may come up over a game because of a switch. True when it had to.
    /// </summary>
    public static bool KeepInFront(IntPtr front, IReadOnlyCollection<int> pids)
    {
        var now = Win32.GetForegroundWindow();
        if (front == IntPtr.Zero || now == front || now == IntPtr.Zero || !pids.Contains(PidOf(now))) return false;
        if (Win32.IsIconic(front)) Win32.ShowWindow(front, Win32.SW_RESTORE); // a full-screen game minimizes when it loses the foreground
        Win32.SetForegroundWindow(front);
        return true;
    }

    private static int PidOf(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out int pid);
        return pid;
    }

    /// <summary>
    /// The process shows a window that answers within a second: it is there and listening, not frozen. An app
    /// showing "Save changes?" still answers, since the dialog box keeps its messages moving.
    /// </summary>
    public static bool Responds(int pid) =>
        Of(pid).Any(window =>
            Win32.SendMessageTimeout(window, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero);
}
