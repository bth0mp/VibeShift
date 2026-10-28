using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VibeShift.Core.Switching;

public sealed record PowerPlan(string Guid, string Name, bool Active);

/// <summary>Windows power plans, through powercfg (switching plans needs no admin).</summary>
public static class PowerPlans
{
    // "Power Scheme GUID: 381b4222-...  (Balanced) *". The label is translated; the GUID and (name) aren't.
    private static readonly Regex Line = new(
        @"([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\s+\((.+)\)[ \t]*(\*)?[ \t]*\r?$", RegexOptions.Multiline);

    public static IReadOnlyList<PowerPlan> Parse(string powercfgList) =>
        Line.Matches(powercfgList).Select(m => new PowerPlan(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success)).ToList();

    public static IReadOnlyList<PowerPlan> List() => Parse(Run("/list"));

    public static void SetActive(string guid)
    {
        // Only a well-formed GUID reaches the command line (it comes from a hand-editable file).
        if (!System.Guid.TryParse(guid, out var g)) throw new ArgumentException($"Not a power plan GUID: {guid}");
        Run("/setactive " + g.ToString("D"));
    }

    // ponytail: powercfg's text output, so a non-ASCII plan name may show garbled (the GUID is what's used);
    // PowerEnumerate + PowerReadFriendlyName if that ever matters
    private static string Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"powercfg {args} failed ({p.ExitCode})");
        return output;
    }
}
