using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeShift.Core.Native;

namespace VibeShift.Core.Processes;

/// <summary>
/// Name is the image file name without ".exe"; PrivateBytes is the private working set; CreateTime is FILETIME UTC;
/// SessionId is the Windows logon session (other users' processes are never VibeShift's to touch).
/// </summary>
public sealed record ProcInfo(int Pid, int ParentPid, string Name, long PrivateBytes, long CreateTime, int SessionId = 0);

public static class ProcessSnapshot
{
    /// <summary>Every process on the box from one NtQuerySystemInformation call (what Task Manager uses). Skips PID 0.</summary>
    public static IReadOnlyList<ProcInfo> Take()
    {
        // ponytail: SYSTEM_PROCESS_INFORMATION offsets below are the 64-bit layout; an x86 build would need its own table
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("VibeShift needs a 64-bit process.");

        int size = 512 * 1024;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = Win32.NtQuerySystemInformation(Win32.SystemProcessInformation, buf, size, out int needed);
                if (status == Win32.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0) throw new Win32Exception($"NtQuerySystemInformation failed: 0x{status:X8}");
                return Parse(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private static unsafe List<ProcInfo> Parse(IntPtr buf)
    {
        var list = new List<ProcInfo>(512);
        byte* p = (byte*)buf;
        while (true)
        {
            int pid = (int)*(nint*)(p + 80);                    // UniqueProcessId
            if (pid != 0)
            {
                ushort len = *(ushort*)(p + 56);                // ImageName.Length (bytes)
                char* name = *(char**)(p + 64);                 // ImageName.Buffer
                string image = name == null ? "" : new string(name, 0, len / 2);
                if (image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) image = image[..^4];
                list.Add(new ProcInfo(
                    pid,
                    (int)*(nint*)(p + 88),                      // InheritedFromUniqueProcessId
                    image,
                    *(long*)(p + 8),                            // WorkingSetPrivateSize
                    *(long*)(p + 32),                           // CreateTime
                    *(int*)(p + 100)));                         // SessionId
            }
            uint next = *(uint*)p;                              // NextEntryOffset
            if (next == 0) break;
            p += next;
        }
        return list;
    }
}
