using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using VibeShift.Core.State;

namespace VibeShift;

/// <summary>Swaps the Dark/Light brush dictionary and colours the title bar to match.</summary>
public static class ThemeManager
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private static ThemeChoice current;
    private static bool hooked;

    public static bool IsDark { get; private set; }

    public static void Apply(ThemeChoice choice)
    {
        current = choice;
        IsDark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemIsDark(),
        };
        var name = IsDark ? "Dark" : "Light";
        Application.Current.Resources.MergedDictionaries[0] =
            new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml") };

        if (!hooked)
        {
            hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (current == ThemeChoice.System && e.Category == UserPreferenceCategory.General)
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
        }
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
    }

    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // Windows 11 only; these calls fail harmlessly on Windows 10.
        int caption = ColorRef((Color)Application.Current.Resources["BgColor"]);
        int text = ColorRef((Color)Application.Current.Resources["TextColor"]);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static bool SystemIsDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
