using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VibeShift;

/// <summary>
/// System-wide Ctrl+Alt shortcuts that work from anywhere, even with VibeShift hidden in the tray or a game in
/// front. Uses the main window's handle, created without showing the window.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr hwnd;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> bindings = [];

    public Hotkeys(Window window)
    {
        hwnd = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(hwnd)!;
        source.AddHook(Hook);
    }

    /// <summary>Registers Ctrl+Alt+key for each entry, replacing earlier ones. Returns the labels another app already owns.</summary>
    public IReadOnlyList<string> Register(IReadOnlyList<(char Key, string Label, Action Run)> keys)
    {
        Unregister();
        var taken = new List<string>();
        int id = 0x5600;
        foreach (var (key, label, run) in keys)
        {
            id++;
            if (RegisterHotKey(hwnd, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, char.ToUpperInvariant(key))) bindings[id] = run;
            else taken.Add(label);
        }
        return taken;
    }

    public void Unregister()
    {
        foreach (var id in bindings.Keys) UnregisterHotKey(hwnd, id);
        bindings.Clear();
    }

    public void Dispose()
    {
        Unregister();
        source.RemoveHook(Hook);
    }

    private IntPtr Hook(IntPtr window, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && bindings.TryGetValue(wParam.ToInt32(), out var run))
        {
            run();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
