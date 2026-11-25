namespace VibeShift.Core.Native;

/// <summary>
/// Ctrl+Alt is how Windows sees AltGr. On a keyboard layout that has one, a Ctrl+Alt shortcut takes over a key people
/// type with: AltGr+V is @ and AltGr+G is ] on Czech, Hungarian and Croatian keyboards, and AltGr+P is ö on
/// US-International. Registered as a shortcut, the character never arrives and VibeShift acts instead
/// (2026-11-25 review: typing ] switched to gaming mode).
/// </summary>
public static class Keyboard
{
    /// <summary>What Ctrl+Alt+<paramref name="key"/> types on any of this user's keyboard layouts, or null if nothing.</summary>
    public static string? AltGrTypes(char key)
    {
        var layouts = new IntPtr[Math.Max(0, Win32.GetKeyboardLayoutList(0, null))];
        int count = layouts.Length == 0 ? 0 : Win32.GetKeyboardLayoutList(layouts.Length, layouts);
        return layouts.Take(count).Select(layout => AltGrTypes(key, layout)).FirstOrDefault(typed => typed is not null);
    }

    /// <summary>The same for one layout (an HKL).</summary>
    public static string? AltGrTypes(char key, IntPtr layout)
    {
        uint virtualKey = char.ToUpperInvariant(key);
        var state = new byte[256];
        state[0x11] = state[0x12] = 0x80; // VK_CONTROL and VK_MENU held down
        var typed = new char[8];
        // Flag 4 leaves the keyboard's own state alone: a dead key looked up here must not land in what is typed next.
        int length = Win32.ToUnicodeEx(virtualKey, Win32.MapVirtualKeyEx(virtualKey, 0, layout), state, typed, typed.Length, 4, layout);
        if (length < 0) length = 1; // a dead key (an accent waiting for its letter): typed with all the same
        return length > 0 && !char.IsControl(typed[0]) ? new string(typed, 0, length) : null;
    }
}
