# VibeShift Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build VibeShift: a one-click Windows app that closes your AI/dev apps for gaming and reopens them afterwards. Ship it as a public GitHub repo with a single-file `.exe` release.

**Architecture:** Two projects:
- `VibeShift.Core` is a class library holding all process, catalog, game and state logic. Every decision lives in pure, unit-tested functions (`SwitchPlanner`, `AutoModeTracker`, `GameRules`). A thin `Switcher` executes those decisions.
- `VibeShift` is the WPF app: a hand-rolled dark/light theme, one window with three pages, and a WinForms `NotifyIcon` tray.

Process data comes from a single native snapshot call (`NtQuerySystemInformation`), not WMI.

**Tech Stack:**
- C# on .NET 10 (`net10.0-windows`), WPF plus WinForms (tray only).
- Tests: xUnit.
- One runtime NuGet package: `System.ServiceProcess.ServiceController`.
- No UI libraries.

**Spec:** `docs/superpowers/specs/2026-10-25-vibeshift-design.md`. Mockups are in `docs/superpowers/specs/mockups/`.

## Global Constraints

- Target `net10.0-windows`. Release: `dotnet publish src/VibeShift -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish`.
- Windows 10 and 11, x64. The app manifest is `asInvoker`, so it never requires admin.
- All state lives in `%APPDATA%\VibeShift\`: `settings.json`, `session.json`, `vibeshift.log`. Nothing is written next to the exe.
- No network access, telemetry or update checks. The only outbound action is the About link, which opens the browser when clicked.
- Idle use: under 1% CPU and under 150 MB working set. When the window is hidden and Auto mode is off, the timer does no work.
- Palette:
  - Dark: bg `#0f0d15`, surface `#1a1724`, raised `#221e2f`, border `#2c2740`, text `#ede9fe`, muted `#9c94b3`, accent `#8b5cf6`, highlight `#c084fc`.
  - Light: bg `#f7f5fc`, surface `#ffffff`, raised `#f1edfb`, border `#e4ddf5`, text `#1e1631`, muted `#6b6385`, accent `#7c3aed`, highlight `#7c3aed`.
  - Button gradient: `#7c3aed` to `#a855f7`.
  - Status dots: coding `#34d399`, gaming `#a855f7`.
  - Font: `Segoe UI Variable, Segoe UI`.
- UI copy, exactly as written:
  - Mode headers: `Vibe coding mode`, `Gaming mode`.
  - Stat tiles: `FREE RAM`, `AI APPS`, `CAN FREE`, `FREED`.
  - List titles: `CLOSE WHEN GAMING`, `CLOSED FOR GAMING`. Add link: `+ Add app`.
  - Row tags: `reopens after`, `come back when their app restarts`, `left running`, `couldn't reopen`.
  - Primary button: `Start gaming`, `Back to vibe coding`.
  - Tray menu: `Start gaming` / `Back to vibe coding`, `Open VibeShift`, `Quit`.
- Defaults:
  - Theme: System (follow Windows).
  - Force-close after: 5 s.
  - Auto mode, Start with Windows, Extra boost: all off.
  - Boost services: `SysMain`, `WSearch`.
  - Auto mode switches back 10 s after the last game exits.
  - Refresh/poll interval: 3 s.
- Commits: conventional (`feat(core): ...`), with a rationale body, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Push to `origin` (GitHub `bth0mp/VibeShift`) after each task.
- **Development safety:** never click Start gaming, or call `Switcher.StartGamingAsync`, with a built-in row switched on. The Claude row closes the Claude session doing this work. Manual checks use the safe settings file from Task 10. Tests never kill anything.

## Decisions this plan adds to the spec

The owner is told about these at handover.
- **MCP detection uses the command-line pattern only.** The spec's "descendant of an AI app" rule is dropped, because it would also kill language servers under VS Code/Cursor and dev servers started from Claude Code.
- **A row is "detected" if it's running now, was seen running before (remembered in settings), or was added by the user.** This replaces per-app install-path tables. An app that isn't running has nothing to close, so switching behaves the same.
- **No Mica backdrop.** The solid palette is what the approved mockups show, and Mica would tint it with the wallpaper. The title bar gets a dark-mode flag and a matching caption color instead.
- **Row icons are colored letter badges** (colors from the mockups), not icons extracted from each exe.

## Review Focus

1. **VibeShift started from a terminal inside an app it closes** (for example, run from Claude Code). VibeShift and its whole parent chain survive, and everything else in that app's tree is killed. Test in Task 7.
2. **Corrupt or half-written `settings.json`/`session.json`.** The app starts with defaults, the bad file is kept as `*.bad`, and saves are atomic (write a temp file, then move). Test in Task 4.
3. **PID reuse.** A process whose recorded parent PID now belongs to a process created after it is not treated as that process's child. The executor checks start time before killing. Test in Task 2.
4. **Game path edge cases:** different casing, a trailing backslash on a library root, a sibling folder sharing a prefix (`common` vs `common2`), and helper exes inside a library. Matching is case-insensitive and on path-segment boundaries. Test in Task 5.
5. **Reopening after a crash or reboot.** `session.json` exists and some closed apps are already running again: only the missing ones are reopened, and MCP/CLI rows are never reopened. Test in Task 7.

---

## File Structure

```
VibeShift/
  VibeShift.sln  LICENSE  README.md  .gitignore
  assets/VibeShift.ico               tools/make-icon.ps1
  docs/screenshots/{dark-coding,light-gaming}.png
  src/VibeShift.Core/
    VibeShift.Core.csproj
    Native/Win32.cs                  P/Invoke declarations only
    Processes/ProcessSnapshot.cs     ProcInfo record + Take()
    Processes/ProcessQuery.cs        image path, command line, AUMID, RAM
    Processes/ProcessTree.cs         descendants, roots, ancestors
    Catalog/Catalog.cs               CatalogEntry, built-in list, Match
    Catalog/McpMatcher.cs
    Catalog/BlockList.cs
    Format.cs                        byte formatting
    State/Settings.cs                Settings, SessionState, ClosedApp
    State/JsonStore.cs
    State/Log.cs
    Games/GameLibraries.cs           discover roots + parsers
    Games/GameRules.cs               IsGame + GameWatcher
    Games/AutoModeTracker.cs
    Switching/SwitchPlanner.cs       Detect, PlanGaming, PlanReopen
    Switching/ReopenTarget.cs
    Switching/Services.cs            service status + elevated helper
    Switching/Switcher.cs            executor (side effects)
  src/VibeShift/
    VibeShift.csproj  app.manifest  App.xaml(.cs)
    Themes/Dark.xaml  Themes/Light.xaml  Themes/Controls.xaml
    ThemeManager.cs  MainWindow.xaml(.cs)  MainViewModel.cs  AppRow.cs
    Tray.cs  StartupRegistration.cs
  tests/VibeShift.Core.Tests/        one *Tests.cs per Core file group
```

Namespaces follow the folders: `VibeShift.Core.Processes`, `.Catalog`, `.State`, `.Games`, `.Switching`, and `VibeShift` for the app.

---

### Task 1: Repo scaffold and native process snapshot

**Files:**
- Create: `VibeShift.sln`, `.gitignore` (`bin/ obj/ publish/ .vs/ *.user .superpowers/`), `LICENSE` (MIT, "Copyright (c) 2026 bth0mp")
- Create: `src/VibeShift.Core/VibeShift.Core.csproj` (`net10.0-windows`, `Nullable enable`, `AllowUnsafeBlocks true`), `Native/Win32.cs`, `Processes/ProcessSnapshot.cs`, `Processes/ProcessQuery.cs`
- Create: `src/VibeShift/VibeShift.csproj` (`WinExe`, `UseWPF`, `UseWindowsForms`, `ImplicitUsings disable` to avoid WPF/WinForms name clashes, `Version 1.0.0`, `ApplicationManifest app.manifest`), `app.manifest` (`asInvoker`, Windows 10/11 supportedOS GUIDs, PerMonitorV2 DPI), and an empty `App.xaml`/`MainWindow.xaml` that compile
- Test: `tests/VibeShift.Core.Tests/VibeShift.Core.Tests.csproj` (xUnit, references Core), `ProcessQueryTests.cs`

**Interfaces (Produces):**
- `public sealed record ProcInfo(int Pid, int ParentPid, string Name, long PrivateBytes, long CreateTime);`
  - `Name` is the image file name without `.exe`, original case.
  - `PrivateBytes` is the private working set.
  - `CreateTime` is FILETIME UTC.
- `ProcessSnapshot.Take() -> IReadOnlyList<ProcInfo>`. Skips PID 0.
- `ProcessQuery.GetImagePath(int pid) -> string?`, `GetCommandLine(int pid) -> string?`, `GetAumid(int pid) -> string?`, `AvailableRamBytes() -> long`, `TotalRamBytes() -> long`. Each returns null on any failure: dead PID, access denied, or not packaged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Snapshot_has_current_process_with_correct_fields() {
    using var me = Process.GetCurrentProcess();
    var p = ProcessSnapshot.Take().Single(x => x.Pid == me.Id);
    Assert.Equal(me.ProcessName, p.Name, ignoreCase: true);
    Assert.Equal(me.StartTime.ToFileTimeUtc(), p.CreateTime);   // proves struct offsets
    Assert.True(p.PrivateBytes > 0);
    Assert.True(p.ParentPid > 0);
}
[Fact] public void Image_path_and_command_line_are_readable_for_self() {
    Assert.Equal(Environment.ProcessPath, ProcessQuery.GetImagePath(Environment.ProcessId), ignoreCase: true);
    Assert.False(string.IsNullOrWhiteSpace(ProcessQuery.GetCommandLine(Environment.ProcessId)));
}
[Fact] public void Unpackaged_self_has_no_aumid() => Assert.Null(ProcessQuery.GetAumid(Environment.ProcessId));
[Fact] public void Dead_pid_gives_nulls() {
    Assert.Null(ProcessQuery.GetImagePath(0x7FFFFFF0));
    Assert.Null(ProcessQuery.GetCommandLine(0x7FFFFFF0));
}
[Fact] public void Ram_is_sane() => Assert.InRange(ProcessQuery.AvailableRamBytes(), 1, ProcessQuery.TotalRamBytes());
```

- [ ] **Step 2:** Run `dotnet test tests/VibeShift.Core.Tests`. Expected: build FAIL (types missing).
- [ ] **Step 3: Implement `Win32.cs`, `ProcessSnapshot.Take()` and `ProcessQuery`**
  - **Snapshot:** call `NtQuerySystemInformation(SystemProcessInformation = 5, ...)` and grow the buffer while it returns `0xC0000004`. Walk the entries using `NextEntryOffset`. x64 offsets within `SYSTEM_PROCESS_INFORMATION`:
    - `WorkingSetPrivateSize` at +8 (long)
    - `CreateTime` at +32 (long)
    - `ImageName` `UNICODE_STRING` at +56 (Length ushort at +56, Buffer pointer at +64)
    - `UniqueProcessId` at +80 (pointer)
    - `InheritedFromUniqueProcessId` at +88 (pointer)
  - **Queries:** open with `PROCESS_QUERY_LIMITED_INFORMATION (0x1000)`.
    - Image path: `QueryFullProcessImageNameW`.
    - Command line: `NtQueryInformationProcess(handle, 60 /*ProcessCommandLineInformation*/, ...)`. The buffer starts with a `UNICODE_STRING` that points into itself. Retry on length mismatch.
    - AUMID: `GetApplicationUserModelId`. Error 15703 means not packaged, so return null.
    - RAM: `GlobalMemoryStatusEx`.
- [ ] **Step 4:** Run `dotnet test tests/VibeShift.Core.Tests`. Expected: 5 passed.
- [ ] **Step 5:** Run `git init`. Commit everything, including `docs/`. Then run `gh repo create bth0mp/VibeShift --public --source . --remote origin --push --description "One click from vibe coding to gaming, and back."`. Expected: the repo URL is printed.

---

### Task 2: Process tree

**Files:** Create `src/VibeShift.Core/Processes/ProcessTree.cs`. Test: `tests/VibeShift.Core.Tests/ProcessTreeTests.cs`.

**Interfaces (Produces):**
- `ProcessTree.Descendants(IReadOnlyList<ProcInfo> all, int rootPid) -> IReadOnlyList<ProcInfo>`: includes the root, children before parents, each process once.
- `ProcessTree.Roots(IReadOnlyList<ProcInfo> all, IReadOnlyCollection<ProcInfo> matched) -> IReadOnlyList<ProcInfo>`: the matched processes whose valid parent is not in `matched`.
- `ProcessTree.Ancestors(IReadOnlyList<ProcInfo> all, int pid) -> IReadOnlySet<int>`: the PID itself plus every valid ancestor.
- Valid parent rule: the parent exists, `parent.Pid != child.Pid`, and `parent.CreateTime <= child.CreateTime`.

- [ ] **Step 1: Write the failing tests** (helper `P(pid, ppid, name, ct = 1, bytes = 10)`)

```csharp
[Fact] public void Descendants_lists_children_before_root_once() {
    var all = new[] { P(1,0,"a"), P(2,1,"b"), P(3,2,"c"), P(4,1,"d") };
    var d = ProcessTree.Descendants(all, 1);
    Assert.Equal(4, d.Count); Assert.Equal(1, d[^1].Pid);
    Assert.True(d.ToList().FindIndex(x => x.Pid == 3) < d.ToList().FindIndex(x => x.Pid == 2));
}
[Fact] public void Child_is_not_attached_to_a_parent_pid_reused_later() {        // Review Focus 3
    var all = new[] { P(10,0,"new", ct: 500), P(20,10,"old", ct: 100) };
    Assert.Single(ProcessTree.Descendants(all, 10));
    Assert.Equal(new[] {20}, ProcessTree.Ancestors(all, 20).ToArray());
}
[Fact] public void Electron_helpers_collapse_to_one_root_and_two_instances_give_two() {
    var all = new[] { P(1,0,"Cursor"), P(2,1,"Cursor"), P(3,1,"Cursor"), P(9,0,"Cursor") };
    Assert.Equal(new[] {1, 9}, ProcessTree.Roots(all, all).Select(r => r.Pid).OrderBy(x => x));
}
```

- [ ] **Step 2:** Run the tests. Expected: FAIL (missing type).
- [ ] **Step 3:** Implement `ProcessTree`. Build a parent-to-children lookup once per call using the valid-parent rule, then walk it depth-first in post-order.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Commit `feat(core): process tree with pid-reuse guard` and push.

---

### Task 3: Catalog, MCP matcher, block list, byte formatting

**Files:** Create `Catalog/Catalog.cs`, `Catalog/McpMatcher.cs`, `Catalog/BlockList.cs`, `Format.cs`. Test: `CatalogTests.cs`.

**Interfaces (Produces):**
- `public enum EntryKind { App, Mcp, DockerWsl, CliAgents, Custom }`
- `public sealed record CatalogEntry(string Id, string Name, EntryKind Kind, string[] ProcessNames, bool DefaultOn, bool Reopen, string Color, string[]? PathContains = null, string[]? PathExcludes = null);`
  - `ProcessNames[0]` is the launcher exe, the only one recorded for reopening.
- `Catalog.BuiltIn : IReadOnlyList<CatalogEntry>`, matched in this order (the first match wins):

| Id | Name | Kind | ProcessNames | Path rule | Default | Reopen | Color |
|---|---|---|---|---|---|---|---|
| claude-desktop | Claude | App | claude | contains `\WindowsApps\Claude_` or `\AnthropicClaude\` | on | yes | #d97757 |
| chatgpt | ChatGPT | App | ChatGPT | none | on | yes | #10a37f |
| cursor | Cursor | App | Cursor | none | on | yes | #3b82f6 |
| windsurf | Windsurf | App | Windsurf | none | on | yes | #14b8a6 |
| copilot | Copilot | App | Copilot, M365Copilot | none | on | yes | #0ea5e9 |
| ollama | Ollama | App | ollama app, ollama | none | on | yes | #6b7280 |
| lmstudio | LM Studio | App | LM Studio, lms | none | on | yes | #6366f1 |
| mcp | MCP servers | Mcp | (via McpMatcher) | none | on | no | #a78bfa |
| vscode | VS Code | App | Code | none | off | yes | #22a6f2 |
| docker-wsl | Docker / WSL | DockerWsl | Docker Desktop, com.docker.backend | none | off | yes | #2496ed |
| cli-agents | Command-line AI agents | CliAgents | claude, codex, gemini | excludes `\WindowsApps\Claude_`, `\AnthropicClaude\` | off | no | #f59e0b |

- `Catalog.Custom(string exePath) -> CatalogEntry`:
  - Id `custom:` plus the lowercased path, Kind Custom.
  - Name from `FileVersionInfo.ProductName`, falling back to the file name.
  - `ProcessNames = [file name without extension]`, `PathContains = [exePath]`.
  - Default on, Reopen yes, Color `#8b5cf6`.
- `Catalog.Match(ProcInfo p, IReadOnlyList<CatalogEntry> entries, Func<int,string?> imagePath, Func<int,string?> commandLine) -> CatalogEntry?`
  - Names compare case-insensitively.
  - A path rule needs the image path. If the path is unreadable, the rule fails (fail-safe).
  - Mcp entries match through `McpMatcher.IsMcp(p.Name, commandLine(p.Pid))`.
- `McpMatcher.IsMcp(string name, string? commandLine) -> bool`
  - The name must be one of `node npx python pythonw python3 uv uvx bun deno cmd`.
  - The command line must match `(?i)mcp|modelcontextprotocol|desktop-commander`.
- `BlockList.IsBlocked(string name, string? imagePath, int pid, int selfPid, IReadOnlySet<int> gamePids) -> bool`. Blocked:
  - `pid == selfPid`, or a pid in `gamePids`.
  - Any path under `%WINDIR%`.
  - These names: `System Registry smss csrss wininit winlogon services lsass svchost dwm explorer sihost fontdrvhost RuntimeBroker ctfmon audiodg MsMpEng SearchHost StartMenuExperienceHost ShellExperienceHost TextInputHost`.
- `Format.Bytes(long) -> string`: under 1 GiB gives whole `MB` (`"640 MB"`); otherwise `GB` with one decimal (`"1.1 GB"`).

- [ ] **Step 1: Write the failing tests**

```csharp
const string Desk = @"C:\Program Files\WindowsApps\Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe";
const string Cli  = @"C:\Users\u\AppData\Roaming\Claude\claude-code\2.1.308\c17a819afb40\claude.exe";
[Fact] public void Claude_desktop_and_cli_are_told_apart_by_path() {
    Assert.Equal("claude-desktop", Match("claude", Desk)?.Id);
    Assert.Equal("cli-agents", Match("claude", Cli)?.Id);
    Assert.Null(Match("claude", path: null));            // unreadable path never matches a path rule
}
[Fact] public void Names_match_case_insensitively() => Assert.Equal("cursor", Match("cursor", @"C:\x\Cursor.exe")?.Id);
[Fact] public void Custom_entry_matches_only_its_exact_path() { /* same name, other folder -> null */ }
[Theory]
[InlineData("node", "\"node\" \"C:\\npm-cache\\@wonderwhy-er\\desktop-commander\\dist\\index.js\"", true)]
[InlineData("uvx", "uvx mcp-server-fetch", true)]
[InlineData("cmd", "cmd /c npx -y @modelcontextprotocol/server-filesystem C:\\", true)]
[InlineData("pythonw", "\"D:\\x\\.venv\\Scripts\\pythonw.exe\" -m coinwatch --data-dir C:\\cw run --host 127.0.0.1 --port 8000", false)]
[InlineData("Code", "Code.exe --mcp", false)]
[InlineData("node", null, false)]
public void Mcp_matching(string name, string? cmd, bool expected) => Assert.Equal(expected, McpMatcher.IsMcp(name, cmd));
[Fact] public void Block_list() {
    var none = new HashSet<int>();
    Assert.True(BlockList.IsBlocked("explorer", @"C:\Windows\explorer.exe", 5, 1, none));
    Assert.True(BlockList.IsBlocked("charmap", Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\charmap.exe"), 5, 1, none));
    Assert.True(BlockList.IsBlocked("VibeShift", null, 1, 1, none));
    Assert.True(BlockList.IsBlocked("deadlock", @"D:\Steam\steamapps\common\Deadlock\deadlock.exe", 7, 1, new HashSet<int>{7}));
    Assert.False(BlockList.IsBlocked("Cursor", @"C:\Program Files\cursor\Cursor.exe", 5, 1, none));
}
[Fact] public void Bytes_format() { Assert.Equal("640 MB", Format.Bytes(671_088_640)); Assert.Equal("1.1 GB", Format.Bytes(1_181_116_006)); Assert.Equal("0 MB", Format.Bytes(0)); }
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the four files.
- [ ] **Step 4:** Run the tests. Expected: all pass. Then sanity-check against live processes: a scratch test marked `[Fact(Skip="manual")]` that prints `Catalog.Match` over `ProcessSnapshot.Take()`. Confirm Claude Desktop matches `claude-desktop` and the `claude.exe` processes under `\Claude\claude-code\` match `cli-agents`. Delete the scratch test afterwards.
- [ ] **Step 5:** Commit `feat(core): app catalog, mcp matcher, block list` and push.

---

### Task 4: Settings, session state, store, log

**Files:** Create `State/Settings.cs`, `State/JsonStore.cs`, `State/Log.cs`. Test: `StateTests.cs`.

**Interfaces (Produces):**
- `public enum ThemeChoice { System, Dark, Light }`
- `Settings` (mutable class, System.Text.Json, enums serialized as strings), defaults as in Global Constraints:
  - `ThemeChoice Theme`
  - `Dictionary<string,bool> Toggles`
  - `List<string> KnownApps`, `CustomApps`, `MyGames`
  - `bool AutoMode`, `StartWithWindows`, `ExtraBoost`
  - `int ForceCloseSeconds`
  - `List<string> BoostServices`
  - `bool IsOn(CatalogEntry e)`: the toggle value if set, otherwise `e.DefaultOn`.
- `public sealed record ClosedApp(string EntryId, string Name, string? ExePath, string? Aumid);`
- `SessionState`: `DateTime StartedUtc`, `bool Automatic`, `long AvailBefore`, `long AvailAfter`, `List<ClosedApp> Closed`, `List<string> StoppedServices`, `bool WslShutDown`, `List<string> ClosedEntryIds` (every entry closed, including MCP/CLI, for row tags).
- `JsonStore(string dir)`: `LoadSettings()`, `SaveSettings(Settings)`, `LoadSession() -> SessionState?`, `SaveSession(SessionState)`, `ClearSession()`, `static JsonStore Default` (`%APPDATA%\VibeShift`, created on demand).
  - Saves write `<file>.tmp`, then `File.Move(tmp, file, overwrite: true)`.
  - A load that fails to parse moves the file to `<file>.bad`, logs it, and returns defaults (settings) or null (session).
- `Log.Init(string dir)`, `Log.Write(string message)`: timestamped lines appended to `vibeshift.log`. Above 1 MB, rename it to `vibeshift.log.old` first. Thread-safe with a lock.

- [ ] **Step 1: Write the failing tests** (temp dir per test)

```csharp
[Fact] public void Missing_files_give_defaults() {
    var s = Store().LoadSettings();
    Assert.Equal(ThemeChoice.System, s.Theme); Assert.Equal(5, s.ForceCloseSeconds);
    Assert.Equal(new[] {"SysMain","WSearch"}, s.BoostServices);
    Assert.False(s.AutoMode || s.ExtraBoost || s.StartWithWindows);
    Assert.Null(Store().LoadSession());
}
[Fact] public void Settings_and_session_round_trip() { /* set Theme=Light, Toggles["mcp"]=false, Closed=[ClosedApp(...)]; reload; assert equal */ }
[Fact] public void Corrupt_files_are_set_aside_and_defaults_used() {                // Review Focus 2
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"Theme\": ");
    File.WriteAllText(Path.Combine(dir, "session.json"), "garbage");
    Assert.Equal(5, Store().LoadSettings().ForceCloseSeconds);
    Assert.Null(Store().LoadSession());
    Assert.True(File.Exists(Path.Combine(dir, "settings.json.bad")));
    Assert.True(File.Exists(Path.Combine(dir, "session.json.bad")));
}
[Fact] public void Save_leaves_no_temp_file() { /* SaveSettings; assert no *.tmp */ }
[Fact] public void IsOn_falls_back_to_entry_default() { /* mcp default on, vscode default off, explicit toggle wins */ }
[Fact] public void Log_rolls_over_at_1MB() { /* write > 1 MB; assert vibeshift.log.old exists */ }
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the three files.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Commit `feat(core): settings, session state and log` and push.

---

### Task 5: Game libraries, game rules, game watcher

**Files:** Create `Games/GameLibraries.cs`, `Games/GameRules.cs`. Test: `GameTests.cs`.

**Interfaces (Produces):**
- `GameLibraries.ParseSteamLibraryFolders(string vdf) -> IReadOnlyList<string>`: regex `"path"\s+"([^"]+)"`, with `\\` unescaped to `\`.
- `GameLibraries.ParseEpicInstallLocation(string itemJson) -> string?`
- `GameLibraries.Discover() -> IReadOnlyList<string>`: existing directories only, no tests (registry and filesystem). Sources:
  - Steam: each library plus `\steamapps\common`. Steam path comes from `HKCU\Software\Valve\Steam\SteamPath`; read `steamapps\libraryfolders.vdf` there.
  - Epic: each `InstallLocation` in `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item`.
  - GOG: `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\*\path`.
  - EA: `<drive>:\Program Files\EA Games` and `<drive>:\EA Games`.
  - Xbox: `<drive>:\XboxGames`, for every fixed drive.
- `GameRules.IsGame(string imagePath, IReadOnlyList<string> roots, IReadOnlyList<string> myGames, bool hasVisibleWindow) -> bool`:
  - Returns true if the path exactly matches an entry in `myGames`, window or not.
  - Otherwise it needs all three: the path is under a root (case-insensitive, on a path-segment boundary, trailing slashes ignored), it isn't excluded, and `hasVisibleWindow` is true.
  - Exclusions match on the file name (wildcards, case-insensitive): `steam steamwebhelper steamservice EpicGamesLauncher EpicWebHelper UnityCrashHandler* CrashReport* *CrashHandler* EasyAntiCheat* BEService* vcredist* DXSETUP *Setup* *Installer* wallpaper32 wallpaper64`.
  - Exclusions also match on any folder segment: `wallpaper_engine`, `Steamworks Shared`, `OBS Studio`, `Soundpad`, `Lossless Scaling`. Mark this list with a `// ponytail:` comment: it's a name list, and the upgrade path is to read the app type from Steam's `appinfo.vdf`.
- `GameWatcher(IReadOnlyList<string> roots, IReadOnlyList<string> myGames, Func<int,string?> imagePath, Func<int,bool> hasVisibleWindow)`, with `Scan(IReadOnlyList<ProcInfo> snapshot) -> IReadOnlyCollection<int>` (PIDs of running games).
  - Caches the image path per `(Pid, CreateTime)` and prunes entries that are gone from the snapshot.
  - Re-checks the window only for processes under a root.

- [ ] **Step 1: Write the failing tests**

```csharp
const string Root = @"D:\SteamLibrary\steamapps\common";
[Fact] public void Steam_vdf_paths_are_unescaped() =>
    Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" },
        GameLibraries.ParseSteamLibraryFolders("\"libraryfolders\"\n{\n\"0\"\n{\n\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n}\n\"1\"\n{\n\"path\"\t\t\"D:\\\\SteamLibrary\"\n}\n}"));
[Fact] public void Epic_manifest_install_location() =>
    Assert.Equal(@"E:\Epic\Fortnite", GameLibraries.ParseEpicInstallLocation("{\"InstallLocation\": \"E:\\\\Epic\\\\Fortnite\"}"));
[Theory]                                                                           // Review Focus 4
[InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\game\bin\win64\deadlock.exe", true, true)]
[InlineData(@"d:\steamlibrary\STEAMAPPS\common\Deadlock\deadlock.exe", true, true)]
[InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\deadlock.exe", false, false)]
[InlineData(@"D:\SteamLibrary\steamapps\common2\Other\other.exe", true, false)]
[InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\UnityCrashHandler64.exe", true, false)]
[InlineData(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper32.exe", true, false)]
public void IsGame(string path, bool window, bool expected) {
    Assert.Equal(expected, GameRules.IsGame(path, new[] { Root }, Array.Empty<string>(), window));
    Assert.Equal(expected, GameRules.IsGame(path, new[] { Root + @"\" }, Array.Empty<string>(), window));
}
[Fact] public void My_games_count_anywhere_without_window() =>
    Assert.True(GameRules.IsGame(@"C:\Riot Games\VALORANT\live\VALORANT.exe", new[] { Root }, new[] { @"c:\riot games\valorant\live\valorant.exe" }, false));
[Fact] public void Watcher_detects_game_once_its_window_appears_and_caches_paths() {
    int pathCalls = 0; bool window = false;
    var w = new GameWatcher(new[] { Root }, Array.Empty<string>(), _ => { pathCalls++; return Root + @"\G\g.exe"; }, _ => window);
    var snap = new[] { new ProcInfo(50, 1, "g", 10, 100) };
    Assert.Empty(w.Scan(snap));
    window = true;
    Assert.Equal(new[] { 50 }, w.Scan(snap));
    Assert.Equal(1, pathCalls);
}
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement both files.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Commit `feat(core): game library discovery and game watcher` and push.

---

### Task 6: Auto mode tracker

**Files:** Create `Games/AutoModeTracker.cs`. Test: `AutoModeTrackerTests.cs`.

**Interfaces (Produces):**
- `public enum Mode { Coding, Gaming }` and `public enum AutoAction { None, StartGaming, BackToCoding }`, both in `VibeShift.Core.Games`.
- `AutoModeTracker(TimeSpan backDelay)`:
  - `AutoAction Update(IReadOnlyCollection<int> gamePids, Mode mode, DateTime nowUtc)`
  - `void ManualSwitch(Mode newMode, IReadOnlyCollection<int> gamePidsNow)`
- Rules:
  - In Coding with any game PID not in the suppressed set: return StartGaming, and mark the session as auto-owned.
  - In Gaming and auto-owned, with no game PIDs: once `backDelay` has passed since games were last seen, return BackToCoding and clear auto-owned. If any game PID appears in the meantime, the timer resets.
  - `ManualSwitch(Coding, pids)` suppresses those PIDs until they exit.
  - `ManualSwitch(Gaming, _)` clears auto-owned, so it never auto-switches back.
  - Suppressed PIDs that are no longer in `gamePids` are dropped.

- [ ] **Step 1: Write the failing tests** (`t0 = DateTime.UtcNow`, `T = new AutoModeTracker(TimeSpan.FromSeconds(10))`)

```csharp
[Fact] public void Game_start_switches_to_gaming() => Assert.Equal(AutoAction.StartGaming, T.Update(new[] {7}, Mode.Coding, t0));
[Fact] public void Switches_back_only_after_ten_quiet_seconds() {
    T.Update(new[] {7}, Mode.Coding, t0);
    Assert.Equal(AutoAction.None, T.Update(new int[0], Mode.Gaming, t0.AddSeconds(1)));
    Assert.Equal(AutoAction.None, T.Update(new int[0], Mode.Gaming, t0.AddSeconds(10)));
    Assert.Equal(AutoAction.BackToCoding, T.Update(new int[0], Mode.Gaming, t0.AddSeconds(11)));
}
[Fact] public void Game_relaunch_within_delay_keeps_gaming() { /* exit at +1, new pid 8 at +5, quiet from +6: None at +15, BackToCoding at +16 */ }
[Fact] public void Manual_back_suppresses_running_game_until_a_new_one() {
    T.Update(new[] {7}, Mode.Coding, t0); T.ManualSwitch(Mode.Coding, new[] {7});
    Assert.Equal(AutoAction.None, T.Update(new[] {7}, Mode.Coding, t0.AddSeconds(3)));
    Assert.Equal(AutoAction.StartGaming, T.Update(new[] {7, 9}, Mode.Coding, t0.AddSeconds(6)));
}
[Fact] public void Manual_gaming_never_auto_returns() {
    T.ManualSwitch(Mode.Gaming, new int[0]); T.Update(new[] {7}, Mode.Gaming, t0);
    Assert.Equal(AutoAction.None, T.Update(new int[0], Mode.Gaming, t0.AddMinutes(5)));
}
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the tracker.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Commit `feat(core): auto mode tracker with manual override` and push.

---

### Task 7: Switch planner and reopen target

**Files:** Create `Switching/SwitchPlanner.cs`, `Switching/ReopenTarget.cs`. Test: `SwitchPlannerTests.cs`.

**Interfaces (Consumes):** `ProcInfo`, `ProcessTree.*` (Task 2); `CatalogEntry`, `Catalog.Match`, `McpMatcher` (Task 3); `Settings`, `SessionState`, `ClosedApp` (Task 4).

**Interfaces (Produces):**
- `public sealed record Detected(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<ProcInfo> Members, long Bytes);`
- `public sealed record CloseTarget(CatalogEntry Entry, IReadOnlyList<ProcInfo> Roots, IReadOnlyList<int> KillPids, IReadOnlyList<ProcInfo> ReopenRoots);`
- `public sealed record GamingPlan(IReadOnlyList<CloseTarget> Close, bool ShutdownWsl, bool PoliteClose, IReadOnlyList<string> StopServices, long EstimatedBytes);`
- `SwitchPlanner.Detect(IReadOnlyList<ProcInfo> all, IReadOnlyList<CatalogEntry> entries, Func<int,string?> imagePath, Func<int,string?> commandLine, Func<ProcInfo,bool> blocked) -> IReadOnlyList<Detected>`
  - Returns one `Detected` per entry that has a match.
  - `Members` is the union of the root trees.
  - `Bytes` is the sum of `Members` with each PID counted once.
  - For `DockerWsl`, processes named `vmmemWSL` are added to `Members` and `Bytes` but never to `Roots`. The entry counts as detected if either Docker or `vmmemWSL` exists.
- `SwitchPlanner.PlanGaming(IReadOnlyList<ProcInfo> all, IReadOnlyList<Detected> detected, Settings s, bool automatic, int selfPid) -> GamingPlan`
  - `Close` covers detected entries with `s.IsOn(entry)` and at least one root.
  - `KillPids` is each root's `Descendants` minus `Ancestors(all, selfPid)`, children first, deduplicated.
  - `ReopenRoots` is the roots whose name equals `ProcessNames[0]`, and only when `Entry.Reopen` is true.
  - `ShutdownWsl` is true when docker-wsl is on and any `vmmemWSL` exists.
  - `PoliteClose` is `!automatic`.
  - `StopServices` is `s.BoostServices` when `s.ExtraBoost && !automatic`, otherwise empty.
  - `EstimatedBytes` is the sum of `Bytes` for the closed entries.
- `SwitchPlanner.PlanReopen(SessionState session, IReadOnlyList<Detected> runningNow) -> IReadOnlyList<ClosedApp>`: `session.Closed` in its original order, minus entries whose `EntryId` has running roots now.
- `ReopenTarget.ToStartInfo(ClosedApp app) -> ProcessStartInfo`
  - With an AUMID: `FileName = "explorer.exe"`, `Arguments = "shell:AppsFolder\\" + aumid`.
  - Otherwise: `FileName = ExePath`, `UseShellExecute = true`, `WorkingDirectory` is the exe's folder.

- [ ] **Step 1: Write the failing tests** (entries from `Catalog.BuiltIn`; path and command-line lambdas from dictionaries)

```csharp
[Fact] public void Only_switched_on_running_rows_close() { /* claude on + vscode off (default) -> Close has claude only */ }
[Fact] public void Two_cursor_instances_are_one_row_with_both_trees() { /* 2 roots, Bytes = both trees */ }
[Fact] public void Kill_list_spares_self_and_its_parent_chain() {                // Review Focus 1
    // Claude 100 -> bash 200 -> VibeShift 300 (self); Claude 100 -> node 201 (mcp)
    var plan = SwitchPlanner.PlanGaming(all, detected, settings, automatic: false, selfPid: 300);
    var kill = plan.Close.Single().KillPids;
    Assert.DoesNotContain(100, kill); Assert.DoesNotContain(200, kill); Assert.DoesNotContain(300, kill);
    Assert.Contains(201, kill);
}
[Fact] public void Kill_list_is_children_first() { /* Cursor 1 -> 2 -> 3: KillPids == [3, 2, 1] */ }
[Fact] public void Automatic_switch_skips_polite_close_and_boost() {
    settings.ExtraBoost = true;
    Assert.Empty(SwitchPlanner.PlanGaming(all, d, settings, true, 1).StopServices);
    Assert.False(SwitchPlanner.PlanGaming(all, d, settings, true, 1).PoliteClose);
    Assert.Equal(new[] {"SysMain","WSearch"}, SwitchPlanner.PlanGaming(all, d, settings, false, 1).StopServices);
}
[Fact] public void Docker_wsl_row_on_closes_docker_and_shuts_down_wsl_off_does_neither() { /* vmmemWSL present both cases */ }
[Fact] public void Only_launcher_roots_of_reopenable_entries_are_recorded() {
    // ollama roots "ollama app" + "ollama" -> ReopenRoots = ["ollama app"]; mcp and cli-agents -> ReopenRoots empty
}
[Fact] public void Reopen_skips_apps_already_running() {                         // Review Focus 5
    var session = new SessionState { Closed = { new("cursor","Cursor",@"C:\c\Cursor.exe",null), new("chatgpt","ChatGPT",null,"OpenAI.ChatGPT_x!App") } };
    var running = /* Detected for cursor with a root */;
    Assert.Equal(new[] {"chatgpt"}, SwitchPlanner.PlanReopen(session, running).Select(c => c.EntryId));
}
[Fact] public void Blocked_processes_are_never_detected() { /* Cursor pid 5 with blocked = p => p.Pid == 5 -> no cursor in Detect result */ }
[Fact] public void Reopen_start_info() {
    var a = ReopenTarget.ToStartInfo(new("chatgpt","ChatGPT",null,"OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT"));
    Assert.Equal("explorer.exe", a.FileName); Assert.Equal(@"shell:AppsFolder\OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT", a.Arguments);
    var b = ReopenTarget.ToStartInfo(new("cursor","Cursor",@"C:\Apps\cursor\Cursor.exe",null));
    Assert.Equal(@"C:\Apps\cursor\Cursor.exe", b.FileName); Assert.True(b.UseShellExecute); Assert.Equal(@"C:\Apps\cursor", b.WorkingDirectory);
}
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement both files.
- [ ] **Step 4:** Run the tests. Expected: all pass.
- [ ] **Step 5:** Commit `feat(core): switch planner and reopen targets` and push.

---

### Task 8: Switcher executor and services helper

**Files:** Create `Switching/Services.cs`, `Switching/Switcher.cs`. Add the `System.ServiceProcess.ServiceController` package to Core. Test: `ServicesTests.cs`.

**Interfaces (Consumes):** everything above.

**Interfaces (Produces):**
- `Services.IsRunning(string name) -> bool`: false if the service is missing.
- `Services.ParseHelperArgs(string[] args) -> (string Verb, string[] Names)?`: accepts exactly `--services stop|start A,B`.
- `Services.RunHelper(string[] args) -> int`: the elevated side. Stops or starts each service with a 15 s wait. Exit 0 if all succeeded, 1 otherwise.
- `Services.RunElevated(string verb, IReadOnlyCollection<string> names) -> bool`: starts `Environment.ProcessPath` with `--services verb A,B`, `Verb = "runas"`, `UseShellExecute = true`, hidden, then waits. Returns false on UAC cancel (`Win32Exception` 1223) or a non-zero exit.
- `Switcher(JsonStore store)`:
  - `Task<SessionState> StartGamingAsync(Settings s, bool automatic, IReadOnlyCollection<int> gamePids, IProgress<string>? progress)`
  - `Task<IReadOnlyList<string>> BackToCodingAsync(Settings s, SessionState session, IProgress<string>? progress)`: returns the names that failed to reopen.
  - Both run on the thread pool.

**StartGamingAsync order:**
1. Measure `AvailBefore`.
2. Take a snapshot, then call `Detect` with `BlockList.IsBlocked(..., Environment.ProcessId, gamePids)`.
3. Call `PlanGaming`.
4. Build `ClosedApp`s from `ReopenRoots` (image path plus `GetAumid`, deduplicated). Set `ClosedEntryIds` to every `plan.Close` entry id. **Save `session.json` before anything is closed.**
5. If `PoliteClose`: call `CloseMainWindow` on each root, then poll every 250 ms for up to `ForceCloseSeconds` until all roots have exited.
6. Kill each surviving `KillPids` entry, but only if `Process.StartTime.ToFileTimeUtc()` still equals the snapshot's `CreateTime`.
7. Run `wsl.exe --shutdown` (no window) if `ShutdownWsl`.
8. Stop services via `RunElevated`, recording those that were running before and are stopped after.
9. Measure `AvailAfter`, save the session again, and log a summary.

Every per-process and per-step failure is caught, logged, and skipped. One failure never stops the rest.

**BackToCodingAsync:**
1. Start `session.StoppedServices` via `RunElevated` (skip if the list is empty).
2. Run `PlanReopen` against a fresh `Detect`, then `Process.Start(ReopenTarget.ToStartInfo(app))` for each app, in order, collecting the names of failures.
3. `ClearSession()`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Helper_args() {
    var (verb, names) = Services.ParseHelperArgs(new[] {"--services","stop","SysMain,WSearch"})!.Value;
    Assert.Equal("stop", verb); Assert.Equal(new[] {"SysMain","WSearch"}, names);
    Assert.Null(Services.ParseHelperArgs(new[] {"--services","delete","x"}));
    Assert.Null(Services.ParseHelperArgs(new[] {"--tray"}));
}
[Fact] public void Missing_service_is_not_running() => Assert.False(Services.IsRunning("NoSuchService_VibeShift"));
```

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement `Services` and `Switcher`.
- [ ] **Step 4:** Run `dotnet test`. Expected: all pass. The executor is checked manually in Task 10.
- [ ] **Step 5:** Commit `feat(core): switcher executor and elevated services helper` and push.

---

### Task 9: WPF themes, Home page, view model, screenshot mode

**Files:** Create `src/VibeShift/Themes/Dark.xaml`, `Themes/Light.xaml`, `Themes/Controls.xaml`, `ThemeManager.cs`, `MainWindow.xaml(.cs)`, `MainViewModel.cs`, `AppRow.cs`. Modify `App.xaml(.cs)`.

**Interfaces (Produces):**
- Theme brush keys, used only as `DynamicResource`:
  - `BgBrush SurfaceBrush RaisedBrush BorderBrush TextBrush MutedBrush AccentBrush HighlightBrush`
  - `ButtonGradientBrush CodingDotBrush GamingDotBrush`
  - Values come from the Global Constraints.
- `Controls.xaml` styles:
  - `ToggleSwitch` (a CheckBox template: 30x17 pill, accent when on; off is `#3a3550` dark, `#d6cfe8` light).
  - `PrimaryButton` (gradient, 10 px radius, purple `DropShadowEffect`), `GhostButton` (outlined accent).
  - `Tile` (Border, 9 px radius), `RailButton`, `RowBorder`.
- `ThemeManager.Apply(ThemeChoice choice)`:
  - Swaps the first merged dictionary.
  - `System` reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` and re-applies on `SystemEvents.UserPreferenceChanged`.
- `ThemeManager.ApplyChrome(Window w)`: `DwmSetWindowAttribute` 20 (immersive dark, matching the theme) and 35 (caption color = bg). Failures are ignored on Windows 10.
- `AppRow : INotifyPropertyChanged`:
  - `string Id, Name, BadgeLetter, BadgeColor, RamText, Tag`
  - `bool IsOn, IsRunning, IsDimmed, IsCustom`
  - The `IsOn` setter calls a save callback.
- `MainViewModel : INotifyPropertyChanged`:
  - `Mode Mode`, `string ModeText`
  - `string FreeRamText, AppsText, ThirdLabel, ThirdValue, ListTitle, PrimaryText`
  - `bool IsBusy`
  - `ObservableCollection<AppRow> HomeRows, AllRows`
  - `Task RefreshAsync()`, `Task ToggleModeAsync(bool automatic)`
  - `void LoadDemo(Mode mode)`: fills the mockup values (Claude 1.1 GB, ChatGPT 640 MB, Cursor 900 MB, MCP servers 550 MB, Docker / WSL 1.9 GB off; tiles 8.2 GB / 4 open / 3.2 GB; gaming: 11.4 GB / 0 open / FREED 3.2 GB).
  - Includes a nested `RelayCommand`.
- `AppsText` is `"{n} open"`, where n is the number of running rows.
- `ThirdValue`: in Coding it's CAN FREE, `PlanGaming(all, detected, settings, automatic: false, Environment.ProcessId).EstimatedBytes`. In Gaming it's FREED, `max(0, AvailAfter - AvailBefore)`.
- The MCP row's tooltip: `Stops every MCP server, including ones used by apps that stay open. Those apps get their tools back when they restart.`
- Home rows:
  - Coding: running rows, plus any row that failed to reopen. Those keep the `couldn't reopen` tag until the app is detected running or the next switch.
  - Gaming: entries in `session.ClosedEntryIds` (dimmed). Tags: `reopens after` if `Reopen`, `come back when their app restarts` for MCP, nothing for CLI agents. Running rows get `left running`.
- When a row is detected running, add its Id to `Settings.KnownApps` and save.
- App args: `--screenshot <png> --theme dark|light --demo coding|gaming`. Renders `MainWindow` off-screen, saves the content with `RenderTargetBitmap` at 2x DPI as PNG, then exits.

Layout, matching `mockups/home-v2.html`:
- Window 480x640, `ResizeMode=CanMinimize`, title `VibeShift`.
- 52 px icon rail (Segoe Fluent Icons: Home `E80F`, Apps `E71D`, Settings `E713`).
- Content padding 16.
- Mode header with a dot, then three tiles in a `UniformGrid`, then the list label and rows, then the primary button.
- While `IsBusy`, the button shows progress text such as `Closing 3 apps...`.

- [ ] **Step 1:** Implement the theme dictionaries, `Controls.xaml`, `ThemeManager`, `AppRow`, `MainViewModel`, `MainWindow` (Home page only; the rail's Apps and Settings buttons are placeholders until Task 10), and the screenshot args in `App.OnStartup`.
- [ ] **Step 2:** Run `dotnet build src/VibeShift -c Debug`. Expected: 0 errors, 0 warnings in project code.
- [ ] **Step 3:** Run `src/VibeShift/bin/Debug/net10.0-windows/VibeShift.exe --screenshot $env:TEMP\vs-dark.png --theme dark --demo coding` and the same with `light`/`gaming`. Open both PNGs with Read and compare them against `docs/superpowers/specs/mockups/home-v2.html` (the layout, colors and copy should match). Fix any mismatch and re-render.
- [ ] **Step 4:** Run `dotnet test`. Expected: still all pass.
- [ ] **Step 5:** Commit `feat(app): themed dashboard home page` and push.

---

### Task 10: Apps and Settings pages, tray, single instance, startup, auto mode, crash resume

**Files:** Create `Tray.cs`, `StartupRegistration.cs`. Modify `MainWindow.xaml(.cs)`, `MainViewModel.cs`, `App.xaml.cs`.

**Interfaces (Produces):**
- `Tray(Action open, Func<Task> toggle, Action quit) : IDisposable`, with `Update(Mode mode)`.
  - Uses the embedded `assets/VibeShift.ico` (a placeholder copy of any `.ico` until Task 11).
  - Tooltip: `VibeShift: Vibe coding mode` / `VibeShift: Gaming mode`.
  - Left-click opens. The menu is listed in Global Constraints.
- `StartupRegistration.Set(bool on)`: writes or deletes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\VibeShift` = `"<Environment.ProcessPath>" --tray`.
- `App.OnStartup` order:
  1. Handle `--services` (call `Services.RunHelper`, then exit) and `--screenshot`.
  2. Take the mutex `Local\VibeShift.Instance`. If it's held, signal `Local\VibeShift.Show` (an `EventWaitHandle`) and exit. The first instance waits on that event in a background thread and shows its window.
  3. Call `Log.Init` and load settings, then `ThemeManager.Apply`.
  4. If `session.json` exists, start in Gaming mode.
  5. Show the window unless `--tray` was passed.
- Closing the window cancels the close and hides it. `Quit` disposes the tray and calls `Shutdown()`.
- One `DispatcherTimer` every 3 s:
  - If the window is visible: run `RefreshAsync`.
  - If `AutoMode`: `GameWatcher.Scan`, then `AutoModeTracker.Update`, then act (`ToggleModeAsync(automatic: true)`).
  - Neither: do nothing.
  - The game roots come from `GameLibraries.Discover()` at startup and again when settings are saved.
  - Manual button presses call `tracker.ManualSwitch`.
  - Automatic switches never `Show()` or `Activate()` the window. Only the tray tooltip changes.
- Apps page:
  - All rows (`AllRows`), with `not running` as the tag when idle.
  - A remove button on custom rows.
  - `+ Add app` uses `OpenFileDialog` (`*.exe`). Picks that `BlockList` rejects show an inline message instead of being added.
- Settings page:
  - Theme (three-way: Follow Windows / Dark / Light).
  - Auto mode toggle plus the My games list (add/remove `.exe`).
  - Start with Windows toggle.
  - Force-close after: ComboBox 3/5/10/15/30 s.
  - Extra boost toggle, with copy explaining that it asks for admin each time you click Start gaming, is skipped for Auto mode switches, and which services it pauses.
  - About: version from the assembly, GitHub link, `MIT License`.

- [ ] **Step 1:** Implement all of the above.
- [ ] **Step 2:** Run `dotnet build`. Expected: 0 errors. Run `dotnet test`. Expected: all pass.
- [ ] **Step 3: Safe live check.**
  1. Back up any existing `%APPDATA%\VibeShift\settings.json`.
  2. Start Notepad. Read its image path from `ProcessQuery.GetImagePath`, which on Windows 11 is a packaged `WindowsApps` path.
  3. Write `settings.json` with every built-in id in `Toggles` set to `false` and `CustomApps = [<notepad path>]`.
  4. Run the Debug exe and click **Start gaming**. Expected:
     - Notepad closes.
     - `session.json` holds Notepad with an AUMID.
     - The Home page shows `Gaming mode` and the `FREED` tile.
     - No other app closed.
  5. Kill VibeShift from Task Manager and start it again. Expected: it opens in `Gaming mode`.
  6. Click **Back to vibe coding**. Expected: Notepad reopens via `shell:AppsFolder` and `session.json` is gone.
  7. Close the window. Expected: the tray icon remains.
  8. Launch the exe a second time. Expected: the first window comes forward and no second tray icon appears.
  9. Restore the original settings.
- [ ] **Step 4:** Render `--screenshot` for the Apps and Settings pages (add `--page apps|settings`) and compare them against the palette and the mockup style.
- [ ] **Step 5:** Commit `feat(app): apps and settings pages, tray, single instance, auto mode` and push.

---

### Task 11: Icon, README, release

**Files:** Create `tools/make-icon.ps1`, `assets/VibeShift.ico`, `docs/screenshots/dark-coding.png`, `docs/screenshots/light-gaming.png`, `README.md`. Modify `src/VibeShift/VibeShift.csproj` (`ApplicationIcon`).

- [ ] **Step 1:** Write `tools/make-icon.ps1`, a PS 5.1, UTF-8-with-BOM script.
  - It draws a rounded square with a `#7c3aed` to `#c084fc` gradient and a white bolt/arrow glyph using System.Drawing, at 16/24/32/48/256 px.
  - It writes a PNG-in-ICO file (ICONDIR plus entries pointing at the PNG bytes).
  - Run it. Expected: `assets/VibeShift.ico` exists, and the exe and tray show it after a rebuild.
- [ ] **Step 2:** Render the README screenshots: `--screenshot docs/screenshots/dark-coding.png --theme dark --demo coding` and `... light-gaming.png --theme light --demo gaming`.
- [ ] **Step 3:** Write `README.md` covering:
  - The tagline.
  - What it is and why (vibe coding to gaming).
  - Both screenshots.
  - **Download**: the Releases link, plus the SmartScreen steps (**More info**, then **Run anyway**) and why they appear (unsigned).
  - What gets closed and reopened, with the catalog table.
  - Auto mode and what counts as a game.
  - Extra boost.
  - Privacy: nothing leaves your PC, no admin needed.
  - Build from source: the publish command from Global Constraints.
  - MIT license.
- [ ] **Step 4:** Run the publish command from Global Constraints. Expected: `publish/VibeShift.exe` exists. Run it once with `--screenshot` to prove the single-file build starts.
- [ ] **Step 5:** Commit `docs: readme, icon and screenshots` and push. Then run `gh release create v1.0.0 publish/VibeShift.exe --title "VibeShift 1.0.0" --notes "<what it does + SmartScreen note>"`. Expected: a release URL with the exe attached.
- [ ] **Step 6:** Add a prose changelog entry for VibeShift 1.0.0 (repo URL, what it does, where it lives) to the Obsidian vault `C:\Users\bth0m\Documents\Olympus Claude`.
