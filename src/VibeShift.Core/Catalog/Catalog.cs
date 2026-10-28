using System.Diagnostics;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Catalog;

public enum EntryKind { App, Mcp, DockerWsl, CliAgents, Custom }

/// <summary>
/// A row VibeShift can close. The exes recorded for reopening are <see cref="Launchers"/>, or ProcessNames[0]
/// when that's null. <see cref="Graceful"/> = false skips the polite close request (browsers restore their tabs
/// after being ended, but forget them when politely closed with default settings).
/// </summary>
public sealed record CatalogEntry(
    string Id,
    string Name,
    EntryKind Kind,
    string[] ProcessNames,
    bool DefaultOn,
    bool Reopen,
    string Color,
    string[]? PathContains = null,
    string[]? PathExcludes = null,
    bool Graceful = true,
    string[]? Launchers = null);

public static class AppCatalog
{
    private static readonly string[] ClaudeDesktopPaths = [@"\WindowsApps\Claude_", @"\AnthropicClaude\"];
    private static readonly string[] Browsers = ["firefox", "chrome", "msedge", "brave", "opera", "vivaldi"];

    /// <summary>Matched in this order; the first match wins.</summary>
    public static IReadOnlyList<CatalogEntry> BuiltIn { get; } =
    [
        new("claude-desktop", "Claude", EntryKind.App, ["claude"], true, true, "#d97757", PathContains: ClaudeDesktopPaths),
        new("chatgpt", "ChatGPT", EntryKind.App, ["ChatGPT"], true, true, "#10a37f"),
        new("cursor", "Cursor", EntryKind.App, ["Cursor"], true, true, "#3b82f6"),
        new("windsurf", "Windsurf", EntryKind.App, ["Windsurf"], true, true, "#14b8a6"),
        new("copilot", "Copilot", EntryKind.App, ["Copilot"], true, true, "#0ea5e9"),
        new("m365copilot", "Microsoft 365 Copilot", EntryKind.App, ["M365Copilot"], true, true, "#2563eb"),
        new("ollama", "Ollama", EntryKind.App, ["ollama app", "ollama"], true, true, "#6b7280"),
        new("lmstudio", "LM Studio", EntryKind.App, ["LM Studio", "lms"], true, true, "#6366f1"),
        new("mcp", "MCP servers", EntryKind.Mcp, [], true, false, "#a78bfa"),
        new("vscode", "VS Code", EntryKind.App, ["Code"], false, true, "#22a6f2"),
        new("docker-wsl", "Docker / WSL", EntryKind.DockerWsl, ["Docker Desktop", "com.docker.backend"], false, true, "#2496ed"),
        new("cli-agents", "Command-line AI agents", EntryKind.CliAgents, ["claude", "codex", "gemini"], false, false, "#f59e0b",
            PathExcludes: ClaudeDesktopPaths),
        // Everyday extras, all opt-in.
        new("browsers", "Web browsers", EntryKind.App, Browsers, false, true, "#f97316", Graceful: false, Launchers: Browsers),
        new("wallpaper-engine", "Wallpaper Engine", EntryKind.App, ["wallpaper32", "wallpaper64"], false, true, "#06b6d4",
            Launchers: ["wallpaper32", "wallpaper64"]),
        new("powertoys", "PowerToys", EntryKind.App, ["PowerToys", "Microsoft.CmdPal.UI"], false, true, "#64748b"),
        // Windows starts Widgets again by itself when it's needed.
        new("widgets", "Windows Widgets", EntryKind.App, ["Widgets", "WidgetService"], false, false, "#0078d4"),
    ];

    private static readonly HashSet<string> AiAppIds =
        ["claude-desktop", "chatgpt", "cursor", "windsurf", "copilot", "m365copilot", "ollama", "lmstudio"];

    /// <summary>Apps that get a live on/off switch on the Vibe page: the AI apps, plus anything the user added.</summary>
    public static bool IsAiApp(CatalogEntry e) => e.Kind == EntryKind.Custom || AiAppIds.Contains(e.Id);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CatalogEntry> CustomCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A row for an exe the user added. Cached per path: reading version info every refresh is wasted I/O.</summary>
    public static CatalogEntry Custom(string exePath) => CustomCache.GetOrAdd(exePath, CreateCustom);

    private static CatalogEntry CreateCustom(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var product = FileVersionInfo.GetVersionInfo(exePath).ProductName;
            if (!string.IsNullOrWhiteSpace(product)) name = product;
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            // keep the file name
        }
        return new("custom:" + exePath.ToLowerInvariant(), name, EntryKind.Custom, [Path.GetFileNameWithoutExtension(exePath)],
            true, true, "#8b5cf6", PathContains: [exePath]);
    }

    public static CatalogEntry? Match(ProcInfo p, IReadOnlyList<CatalogEntry> entries, Func<int, string?> imagePath, Func<int, string?> commandLine)
    {
        string? path = null, cmd = null;
        bool pathRead = false, cmdRead = false;
        foreach (var e in entries)
        {
            // Command lines are only read for script runtimes (node, python...), never for every process.
            if (e.Kind == EntryKind.Mcp)
            {
                if (McpMatcher.IsRuntime(p.Name) && McpMatcher.IsMcp(p.Name, Cmd())) return e;
                continue;
            }
            if (e.Kind == EntryKind.CliAgents && McpMatcher.IsRuntime(p.Name) && McpMatcher.IsNpmAgent(p.Name, Cmd())) return e;
            if (!e.ProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (e.PathContains is null && e.PathExcludes is null) return e;

            if (!pathRead) { path = imagePath(p.Pid); pathRead = true; }
            if (path is null) continue; // fail-safe: a path rule never matches an unreadable path
            if (e.PathContains is { } inc && !inc.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (e.PathExcludes is { } exc && exc.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            return e;
        }
        return null;

        string? Cmd()
        {
            if (!cmdRead) { cmd = commandLine(p.Pid); cmdRead = true; }
            return cmd;
        }
    }
}
