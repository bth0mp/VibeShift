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

    // "mcp" only as its own token (weather-mcp, mcp_server.py), never inside a word like McPherson or mcpe.
    [GeneratedRegex("(?<![a-z])mcp(?![a-z])|modelcontextprotocol|desktop-commander", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    // npm-installed AI agents run as node; they are command-line agents, not MCP servers. Anchored on the
    // agents' entry scripts so "npm install -g @anthropic-ai/claude-code" isn't mistaken for the agent.
    [GeneratedRegex(@"@anthropic-ai[\\/]claude-code[\\/]cli\.js|@google[\\/]gemini-cli[\\/](dist|bundle)[\\/]|@openai[\\/]codex[\\/]bin[\\/]",
        RegexOptions.IgnoreCase)]
    private static partial Regex NpmAgent();

    public static bool IsRuntime(string name) => Runtimes.Contains(name);

    // The executable itself, quoted or not: "mcp" in the interpreter's path (a conda env named mcp) says nothing.
    [GeneratedRegex(@"^\s*(""[^""]*""|\S+)")]
    private static partial Regex Executable();

    // A path into node_modules counts from the package on: the project around it (my-mcp-app) isn't the server.
    [GeneratedRegex(@"[^\s""]*node_modules[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex UpToPackage();

    // ponytail: an MCP server whose only "mcp" is its venv's python.exe path is missed (it keeps running); that's
    // the safe side, since a wrong match kills a notebook kernel or dev server for good
    public static bool IsMcp(string name, string? commandLine) =>
        commandLine is not null && IsRuntime(name) && Pattern().IsMatch(UpToPackage().Replace(Executable().Replace(commandLine, ""), ""))
        && !NpmAgent().IsMatch(commandLine);

    public static bool IsNpmAgent(string name, string? commandLine) =>
        commandLine is not null && name.Equals("node", StringComparison.OrdinalIgnoreCase) && NpmAgent().IsMatch(commandLine);
}
