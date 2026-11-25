using System.Runtime.InteropServices;
using VibeShift.Core.Native;

namespace VibeShift.Core.Tests;

public class KeyboardTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string id, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnloadKeyboardLayout(IntPtr layout);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? layouts);

    private const uint NoTellShell = 0x80; // KLF_NOTELLSHELL

    [Fact] // 2026-11-25 review: on a Hungarian keyboard AltGr+V is @ and AltGr+G is ], and AltGr is Ctrl+Alt
    public void A_Ctrl_Alt_shortcut_that_types_a_character_is_found()
    {
        // The look-up is read-only and runs everywhere, on whatever keyboards this PC has (it must not throw).
        _ = Keyboard.AltGrTypes('G');
        // Loading a keyboard layout adds it to the whole sign-in session, so the rest runs only on CI's throwaway one:
        // a developer's language list is left alone.
        if (Environment.GetEnvironmentVariable("CI") is null) return;

        var before = new IntPtr[GetKeyboardLayoutList(0, null)];
        GetKeyboardLayoutList(before.Length, before);
        IntPtr hungarian = LoadKeyboardLayout("0000040e", NoTellShell), us = LoadKeyboardLayout("00000409", NoTellShell);
        try
        {
            Assert.NotEqual(IntPtr.Zero, hungarian);
            Assert.NotEqual(us, hungarian);
            Assert.Equal("@", Keyboard.AltGrTypes('V', hungarian));
            Assert.Equal("]", Keyboard.AltGrTypes('G', hungarian));
            foreach (char key in "GVP") Assert.Null(Keyboard.AltGrTypes(key, us));
        }
        finally
        {
            foreach (var layout in new[] { hungarian, us })
                if (layout != IntPtr.Zero && !before.Contains(layout)) UnloadKeyboardLayout(layout);
        }
    }
}
