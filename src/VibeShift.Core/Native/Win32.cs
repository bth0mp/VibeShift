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
    internal const int JobObjectBasicProcessIdList = 3;

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(SafeProcessHandle process, ref int length, [Out] char[]? buffer);

    [DllImport("user32.dll")]
    internal static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? layouts);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, [Out] char[] buffer, int size, uint flags, IntPtr layout);

    internal const int GW_OWNER = 4;
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const int DWMWA_CLOAKED = 14;
    internal const int DWM_CLOAKED_APP = 1;

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr window, int relation);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr window, int command);

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
