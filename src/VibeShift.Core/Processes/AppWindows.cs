using VibeShift.Core.Native;

namespace VibeShift.Core.Processes;

/// <summary>An app's windows: asking all of them to close, and telling whether the app is still there to answer.</summary>
public static class AppWindows
{
    /// <summary>
    /// The process's visible top-level windows that no other window owns: what Process.MainWindowHandle picks one of.
    /// A dialog box is owned by the window under it, so it isn't listed; a window hidden to the tray isn't visible.
    /// </summary>
    public static IReadOnlyList<IntPtr> Of(int pid)
    {
        var found = new List<IntPtr>();
        Win32.EnumWindows((window, _) =>
        {
            Win32.GetWindowThreadProcessId(window, out int owner);
            if (owner == pid && Win32.IsWindowVisible(window) && Win32.GetWindow(window, Win32.GW_OWNER) == IntPtr.Zero) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

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

    /// <summary>
    /// The process shows a window that answers within a second: it is there and listening, not frozen. An app
    /// showing "Save changes?" still answers, since the dialog box keeps its messages moving.
    /// </summary>
    public static bool Responds(int pid) =>
        Of(pid).Any(window =>
            Win32.SendMessageTimeout(window, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero);
}
