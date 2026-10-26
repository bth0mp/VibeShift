using System.Diagnostics;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Catalog;

public enum EntryKind { App, Mcp, DockerWsl, CliAgents, Custom }

/// <summary>A row VibeShift can close. ProcessNames[0] is the launcher exe, the only one recorded for reopening.</summary>
public sealed record CatalogEntry(
    string Id,
    string Name,
    EntryKind Kind,
    string[] ProcessNames,
    bool DefaultOn,
    bool Reopen,
    string Color,
    string[]? PathContains = null,
    string[]? PathExcludes = null);

public static class AppCatalog
{
    private static readonly string[] ClaudeDesktopPaths = [@"\WindowsApps\Claude_", @"\AnthropicClaude\"];

    /// <summary>Matched in this order; the first match wins.</summary>
    public static IReadOnlyList<CatalogEntry> BuiltIn { get; } =
    [
        new("claude-desktop", "Claude", EntryKind.App, ["claude"], true, true, "#d97757", PathContains: ClaudeDesktopPaths),
        new("chatgpt", "ChatGPT", EntryKind.App, ["ChatGPT"], true, true, "#10a37f"),
        new("cursor", "Cursor", EntryKind.App, ["Cursor"], true, true, "#3b82f6"),
        new("windsurf", "Windsurf", EntryKind.App, ["Windsurf"], true, true, "#14b8a6"),
        new("copilot", "Copilot", EntryKind.App, ["Copilot", "M365Copilot"], true, true, "#0ea5e9"),
        new("ollama", "Ollama", EntryKind.App, ["ollama app", "ollama"], true, true, "#6b7280"),
        new("lmstudio", "LM Studio", EntryKind.App, ["LM Studio", "lms"], true, true, "#6366f1"),
        new("mcp", "MCP servers", EntryKind.Mcp, [], true, false, "#a78bfa"),
        new("vscode", "VS Code", EntryKind.App, ["Code"], false, true, "#22a6f2"),
        new("docker-wsl", "Docker / WSL", EntryKind.DockerWsl, ["Docker Desktop", "com.docker.backend"], false, true, "#2496ed"),
        new("cli-agents", "Command-line AI agents", EntryKind.CliAgents, ["claude", "codex", "gemini"], false, false, "#f59e0b",
            PathExcludes: ClaudeDesktopPaths),
    ];

    public static CatalogEntry Custom(string exePath)
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
        string? path = null;
        bool pathRead = false;
        foreach (var e in entries)
        {
            if (e.Kind == EntryKind.Mcp)
            {
                if (McpMatcher.IsMcp(p.Name, commandLine(p.Pid))) return e;
                continue;
            }
            if (!e.ProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (e.PathContains is null && e.PathExcludes is null) return e;

            if (!pathRead) { path = imagePath(p.Pid); pathRead = true; }
            if (path is null) continue; // fail-safe: a path rule never matches an unreadable path
            if (e.PathContains is { } inc && !inc.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (e.PathExcludes is { } exc && exc.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
            return e;
        }
        return null;
    }
}
