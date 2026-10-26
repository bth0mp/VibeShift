using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using VibeShift.Core.State;

namespace VibeShift.Core.Switching;

/// <summary>
/// Extra boost: pausing Windows services needs admin, so VibeShift relaunches itself elevated
/// with --services for that one step and stays unelevated otherwise.
/// </summary>
public static class Services
{
    private const int ErrorCancelled = 1223;

    /// <summary>The only services the elevated helper will touch, whatever settings.json says.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "SysMain", "WSearch" };

    public static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false; // no such service
        }
    }

    public static (string Verb, string[] Names)? ParseHelperArgs(string[] args)
    {
        if (args.Length != 3 || args[0] != "--services" || args[1] is not ("stop" or "start")) return null;
        var names = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 || !names.All(Allowed.Contains) ? null : (args[1], names);
    }

    /// <summary>The elevated side. Exit 0 if every service reached the wanted state.</summary>
    public static int RunHelper(string[] args)
    {
        if (ParseHelperArgs(args) is not { } parsed) return 2;
        var (verb, names) = parsed;
        var want = verb == "stop" ? ServiceControllerStatus.Stopped : ServiceControllerStatus.Running;
        bool ok = true;
        foreach (var name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == want) continue;
                if (verb == "stop") sc.Stop(); else sc.Start();
                sc.WaitForStatus(want, TimeSpan.FromSeconds(15));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
            {
                ok = false;
            }
        }
        return ok ? 0 : 1;
    }

    /// <summary>Runs the helper behind one UAC prompt. False on cancel or any failure.</summary>
    public static bool RunElevated(string verb, IReadOnlyCollection<string> names)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--services {verb} \"{string.Join(',', names)}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            Log.Write($"Extra boost: admin prompt cancelled ({verb} {string.Join(',', names)})");
            return false;
        }
    }
}
