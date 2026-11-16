using System;
using Microsoft.Win32;

namespace VibeShift;

/// <summary>Start with Windows: a per-user Run key that launches VibeShift quietly into the tray.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "VibeShift";

    public static void Set(bool on)
    {
        // A copy with its own data folder (a test run, a portable copy) must not repoint the real one's Run entry
        // at itself: the next sign-in would start that exe against the user's real data.
        if (Environment.GetEnvironmentVariable(VibeShift.Core.State.JsonStore.DataDirVariable) is { Length: > 0 }) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
