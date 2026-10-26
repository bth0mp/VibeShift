using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VibeShift.Core.Native;

namespace VibeShift.Core.Processes;

/// <summary>Per-process lookups. Each returns null on any failure: dead PID, access denied, not packaged.</summary>
public static class ProcessQuery
{
    public static string? GetImagePath(int pid)
    {
        using var h = Open(pid);
        if (h is null) return null;
        var buf = new char[4096];
        int size = buf.Length;
        return Win32.QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }

    public static unsafe string? GetCommandLine(int pid)
    {
        using var h = Open(pid);
        if (h is null) return null;
        int size = 2048;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = Win32.NtQueryInformationProcess(h, Win32.ProcessCommandLineInformation, buf, size, out int needed);
                if (status is Win32.STATUS_INFO_LENGTH_MISMATCH or Win32.STATUS_BUFFER_TOO_SMALL or Win32.STATUS_BUFFER_OVERFLOW)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }
                if (status != 0) return null;
                // The buffer starts with a UNICODE_STRING whose Buffer points into this same allocation.
                ushort len = *(ushort*)buf;
                char* s = *(char**)((byte*)buf + 8);
                return s == null ? "" : new string(s, 0, len / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
    }

    /// <summary>AppUserModelID of a packaged (MSIX/Store) process, used to relaunch it via shell:AppsFolder.</summary>
    public static string? GetAumid(int pid)
    {
        using var h = Open(pid);
        if (h is null) return null;
        int len = 0;
        if (Win32.GetApplicationUserModelId(h, ref len, null) != Win32.ERROR_INSUFFICIENT_BUFFER) return null;
        var buf = new char[len];
        return Win32.GetApplicationUserModelId(h, ref len, buf) == 0 ? new string(buf, 0, len - 1) : null;
    }

    public static long AvailableRamBytes() => (long)MemoryStatus().ullAvailPhys;

    public static long TotalRamBytes() => (long)MemoryStatus().ullTotalPhys;

    private static Win32.MEMORYSTATUSEX MemoryStatus()
    {
        var s = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref s)) throw new System.ComponentModel.Win32Exception();
        return s;
    }

    private static SafeProcessHandle? Open(int pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }
}
