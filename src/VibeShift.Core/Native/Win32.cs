using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeShift.Core.Native;

internal static class Win32
{
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const int SystemProcessInformation = 5;
    internal const int ProcessCommandLineInformation = 60;
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    internal const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    internal const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(SafeProcessHandle process, ref int length, [Out] char[]? buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
