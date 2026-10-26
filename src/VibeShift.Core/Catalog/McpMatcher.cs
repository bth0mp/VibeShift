using System.Text.RegularExpressions;

namespace VibeShift.Core.Catalog;

/// <summary>
/// MCP servers are stdio runtimes (node, python, uv...) launched by AI apps. They're recognized by
/// command line only, so unrelated node/python work (language servers, dev servers, scripts) survives.
/// </summary>
public static partial class McpMatcher
{
    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
        { "node", "npx", "python", "pythonw", "python3", "uv", "uvx", "bun", "deno", "cmd" };

    [GeneratedRegex("mcp|modelcontextprotocol|desktop-commander", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool IsMcp(string name, string? commandLine) =>
        commandLine is not null && Runtimes.Contains(name) && Pattern().IsMatch(commandLine);
}
