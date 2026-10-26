using VibeShift.Core.Catalog;
using VibeShift.Core.Processes;

namespace VibeShift.Core.Tests;

public class CatalogTests
{
    private const string Desk = @"C:\Program Files\WindowsApps\Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
    private const string Cli = @"C:\Users\u\AppData\Roaming\Claude\claude-code\2.1.308\c17a819afb40\claude.exe";

    private static CatalogEntry? Match(string name, string? path, IReadOnlyList<CatalogEntry>? entries = null) =>
        AppCatalog.Match(new ProcInfo(42, 1, name, 10, 1), entries ?? AppCatalog.BuiltIn, _ => path, _ => null);

    [Fact]
    public void Claude_desktop_and_cli_are_told_apart_by_path()
    {
        Assert.Equal("claude-desktop", Match("claude", Desk)?.Id);
        Assert.Equal("cli-agents", Match("claude", Cli)?.Id);
        Assert.Null(Match("claude", path: null)); // unreadable path never matches a path rule
    }

    [Fact]
    public void Names_match_case_insensitively() => Assert.Equal("cursor", Match("cursor", @"C:\x\Cursor.exe")?.Id);

    [Fact]
    public void Custom_entry_matches_only_its_exact_path()
    {
        var entries = new[] { AppCatalog.Custom(@"C:\Tools\Thing\thing.exe") };
        Assert.Equal(@"custom:c:\tools\thing\thing.exe", Match("thing", @"C:\Tools\Thing\thing.exe", entries)?.Id);
        Assert.Null(Match("thing", @"C:\Other\thing.exe", entries));
    }

    [Fact]
    public void Custom_entry_defaults()
    {
        var e = AppCatalog.Custom(@"C:\Tools\Thing\thing.exe");
        Assert.Equal(EntryKind.Custom, e.Kind);
        Assert.Equal("thing", e.Name); // no version info on a missing file -> file name
        Assert.True(e.DefaultOn);
        Assert.True(e.Reopen);
    }

    [Theory]
    [InlineData("node", "\"node\" \"C:\\npm-cache\\@wonderwhy-er\\desktop-commander\\dist\\index.js\"", true)]
    [InlineData("uvx", "uvx mcp-server-fetch", true)]
    [InlineData("cmd", "cmd /c npx -y @modelcontextprotocol/server-filesystem C:\\", true)]
    [InlineData("pythonw", "\"D:\\x\\.venv\\Scripts\\pythonw.exe\" -m coinwatch --data-dir C:\\cw run --host 127.0.0.1 --port 8000", false)]
    [InlineData("Code", "Code.exe --mcp", false)]
    [InlineData("node", null, false)]
    // review I2: "mcp" must be its own token, not part of a user or folder name
    [InlineData("node", "node C:\\Users\\McPherson\\app.js", false)]
    [InlineData("node", "node D:\\games\\mcpe\\server.js", false)]
    [InlineData("python", "python mcp_server.py", true)]
    [InlineData("node", "node C:\\x\\weather-mcp\\index.js", true)]
    public void Mcp_matching(string name, string? cmd, bool expected) => Assert.Equal(expected, McpMatcher.IsMcp(name, cmd));

    [Fact] // M12: command lines cost a process handle; only runtimes need one
    public void Non_runtime_processes_never_have_their_command_line_read()
    {
        int calls = 0;
        AppCatalog.Match(new ProcInfo(42, 1, "notepad2", 10, 1), AppCatalog.BuiltIn, _ => null, _ => { calls++; return null; });
        Assert.Equal(0, calls);
    }

    [Fact] // M12: custom entries read file version info once, not every refresh
    public void Custom_entries_are_cached() => Assert.Same(AppCatalog.Custom(@"C:\T\x.exe"), AppCatalog.Custom(@"C:\T\x.exe"));

    [Theory] // M13: npm-installed agents run as node; they're CLI agents, not MCP servers
    [InlineData("node \"C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" --mcp-config m.json")]
    [InlineData("node C:/npm/node_modules/@google/gemini-cli/dist/index.js")]
    [InlineData("node C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js")]
    public void Npm_installed_agents_are_cli_agents(string cmd) =>
        Assert.Equal("cli-agents", AppCatalog.Match(new ProcInfo(42, 1, "node", 10, 1), AppCatalog.BuiltIn, _ => null, _ => cmd)?.Id);

    [Theory] // 1.1: browsers and desktop extras rows
    [InlineData("firefox", "browsers")]
    [InlineData("chrome", "browsers")]
    [InlineData("msedge", "browsers")]
    [InlineData("wallpaper64", "wallpaper-engine")]
    [InlineData("PowerToys", "powertoys")]
    [InlineData("Microsoft.CmdPal.UI", "powertoys")]
    [InlineData("Widgets", "widgets")]
    public void Extra_rows_match(string name, string id) => Assert.Equal(id, Match(name, @"C:\x\" + name + ".exe")?.Id);

    [Fact] // 1.1: every new row is opt-in
    public void Extra_rows_are_off_by_default() =>
        Assert.All(AppCatalog.BuiltIn.Where(e => e.Id is "browsers" or "wallpaper-engine" or "powertoys" or "widgets"), e => Assert.False(e.DefaultOn));

    [Fact] // 1.0.1 review #5: updating an agent with npm isn't running the agent
    public void Installing_an_agent_with_npm_is_not_the_agent() =>
        Assert.Null(AppCatalog.Match(new ProcInfo(42, 1, "node", 10, 1), AppCatalog.BuiltIn, _ => null,
            _ => "node C:\\nodejs\\node_modules\\npm\\bin\\npm-cli.js install -g @anthropic-ai/claude-code"));

    [Fact]
    public void Mcp_entry_matches_through_command_line()
    {
        var p = new ProcInfo(42, 1, "node", 10, 1);
        Assert.Equal("mcp", AppCatalog.Match(p, AppCatalog.BuiltIn, _ => null, _ => "node mcp-server-x")?.Id);
        Assert.Null(AppCatalog.Match(p, AppCatalog.BuiltIn, _ => null, _ => "node server.js"));
    }

    [Fact]
    public void Block_list()
    {
        var none = new HashSet<int>();
        Assert.True(BlockList.IsBlocked("explorer", @"C:\Windows\explorer.exe", 5, 1, none));
        Assert.True(BlockList.IsBlocked("charmap", Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\charmap.exe"), 5, 1, none));
        Assert.True(BlockList.IsBlocked("VibeShift", null, 1, 1, none));
        Assert.True(BlockList.IsBlocked("deadlock", @"D:\Steam\steamapps\common\Deadlock\deadlock.exe", 7, 1, new HashSet<int> { 7 }));
        Assert.False(BlockList.IsBlocked("Cursor", @"C:\Program Files\cursor\Cursor.exe", 5, 1, none));
    }

    [Fact]
    public void Bytes_format()
    {
        Assert.Equal("640 MB", Format.Bytes(671_088_640));
        Assert.Equal("1.1 GB", Format.Bytes(1_181_116_006));
        Assert.Equal("0 MB", Format.Bytes(0));
    }
}
