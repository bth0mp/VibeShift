# Protect Unsaved Work Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An app that answers VibeShift's close request with a question stays open, with everything under it, and the user is told which app.

**Architecture:** The planner decides who is asked (now automatic switches too) and which processes a left-open root shields from the kill list; both are pure and unit-tested. The switcher asks every window of each root, waits as today, then keeps the roots that still show a responding window. The session carries the left-open row ids to the UI.

**Tech Stack:** C# / .NET 10, WPF, xUnit, user32 P/Invoke. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-27-protect-unsaved-work-design.md`

## Global Constraints

- Names: `Settings.KeepAppsThatAsk` (bool, default `true`), `SessionState.LeftOpen` (`List<string>` of entry ids).
- Exact copy:
  - Log: `Left open (asked a question): [Visual Studio]`
  - Home: `Still open: Visual Studio. It asked a question instead of closing.` (several apps: names joined with `, `, and `They asked`)
  - Notification title: `Visual Studio is still open` (several: joined with ` and `, `are still open`); text: `It asked a question instead of closing, so VibeShift left it alone.` (several: `They asked`)
  - Setting: `Leave an app open when it asks a question`; hint: `Such as "Save changes?". Turn this off to force-close it anyway.`
  - Force-close hint becomes: `Apps get this long to close on their own.`
- `KeepAppsThatAsk = false` must behave exactly as 047ab97 does: manual switches ask one... every window, wait, then end everything; automatic switches end everything at once.
- Browsers (`Graceful: false`) are never asked. Command-line agents have no window and are ended as today.
- Live checks only with `VIBESHIFT_DATA` isolation, never while a game runs, never against the user's own apps. Build with the scratchpad `build.ps1` (BelowNormal priority). Don't rebuild while live checks run.
- Commits: conventional style with a rationale body, `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. Push to main once, after the review.

## Review Focus

1. An app that exits (or whose question is answered) between the last look and the kill pass: no exception, counted as closed. Test: `AppWindows.Responds` on a PID that doesn't exist is `false` (Task 2).
2. Processes under a left-open app that other rows matched (an MCP server under VS Code) must survive with it. Test: `Kill_order_leaves_out_everything_under_an_app_left_open` (Task 1).
3. A `session.json` or `settings.json` written by 1.10.1 (neither new property): loads as `LeftOpen = []`, `KeepAppsThatAsk = true`. Test: `Files_from_before_this_feature_load_with_its_defaults` (Task 1).
4. An app that is frozen, or hides to the tray, must still be force-closed. Tests: live checks `freeze` and `hide` (Task 3).
5. An app with two windows and no question must close completely, not be read as asking. Test: live check `two` (Task 3).

---

### Task 1: Planner, settings and session

**Files:**
- Modify: `src/VibeShift.Core/State/Settings.cs`, `src/VibeShift.Core/Switching/SwitchPlanner.cs`
- Test: `tests/VibeShift.Core.Tests/SwitchPlannerTests.cs`, `tests/VibeShift.Core.Tests/StateTests.cs`

**Interfaces:**
- Produces: `Settings.KeepAppsThatAsk`; `SessionState.LeftOpen` (null entries dropped in `Normalize`);
  `SwitchPlanner.KillOrder(IReadOnlyList<ProcInfo> all, GamingPlan plan, IReadOnlyCollection<int>? leftOpenRoots = null)`;
  `SwitchPlanner.LeftOpen(GamingPlan plan, IReadOnlyCollection<int> leftOpenRoots) : IReadOnlyList<CatalogEntry>` (the close targets with a root in the set).
  `PlanGaming`: `PoliteClose` and `PoliteCloseRoots` no longer depend on `automatic` alone: asked when `!automatic || s.KeepAppsThatAsk`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SwitchPlannerTests: replaces Automatic_switch_skips_polite_close_and_boost
[Fact]
public void Automatic_switch_asks_apps_to_close_but_skips_boost()
{
    var all = new[] { P(1, 0, "Cursor") };
    settings.ExtraBoost = true;
    Assert.Empty(Plan(all, automatic: true).StopServices);
    Assert.True(Plan(all, automatic: true).PoliteClose);
    Assert.Equal(new[] { 1 }, Plan(all, automatic: true).PoliteCloseRoots.Select(r => r.Pid));
    settings.KeepAppsThatAsk = false; // as before: an automatic switch ends everything at once
    Assert.False(Plan(all, automatic: true).PoliteClose);
    Assert.Empty(Plan(all, automatic: true).PoliteCloseRoots);
    Assert.True(Plan(all, automatic: false).PoliteClose);
    Assert.Equal(new[] { "SysMain", "WSearch" }, Plan(all, automatic: false).StopServices);
}

[Fact]
public void Kill_order_leaves_out_everything_under_an_app_left_open()
{
    var all = new[] { P(1, 0, "Code"), P(2, 1, "node"), P(3, 0, "Cursor"), P(4, 3, "Cursor") };
    paths[1] = @"C:\x\Code.exe";
    cmds[2] = "node mcp-server-x";
    settings.Toggles["vscode"] = true;
    var plan = Plan(all);
    Assert.Equal(new[] { 3, 4 }, SwitchPlanner.KillOrder(all, plan, [1]));
    Assert.Equal(new[] { "vscode" }, SwitchPlanner.LeftOpen(plan, [1]).Select(e => e.Id));
    Assert.Empty(SwitchPlanner.LeftOpen(plan, []));
}
```

In `Browsers_are_not_asked_to_close_politely`, the automatic case becomes `Assert.Equal(new[] { 2 }, ...)` (the browser still isn't asked).

```csharp
// StateTests
[Fact]
public void Files_from_before_this_feature_load_with_its_defaults()
{
    Assert.True(JsonSerializer.Deserialize<Settings>("{\"ForceCloseSeconds\": 10}")!.KeepAppsThatAsk);
    Assert.Empty(JsonSerializer.Deserialize<SessionState>("{\"Automatic\": true}")!.LeftOpen);
}
```

- [ ] **Step 2: Run them, expect compile failures** — `dotnet test VibeShift.sln -c Release --filter "FullyQualifiedName~SwitchPlannerTests|FullyQualifiedName~StateTests"`
- [ ] **Step 3: Implement** the two properties, the `KillOrder` parameter (drop every PID in `ProcessTree.Descendants(all, root)` for each left-open root), `LeftOpen`, and the `PlanGaming` condition.
- [ ] **Step 4: Run all unit tests, expect pass** — `dotnet test VibeShift.sln -c Release`
- [ ] **Step 5: Commit** — `feat(core): plan for apps that ask before closing`

### Task 2: Ask every window, keep what is asking

**Files:**
- Create: `src/VibeShift.Core/Processes/AppWindows.cs`
- Modify: `src/VibeShift.Core/Native/Win32.cs`, `src/VibeShift.Core/Switching/Switcher.cs`
- Test: `tests/VibeShift.Core.Tests/ProcessQueryTests.cs`

**Interfaces:**
- Consumes: Task 1's `KillOrder`, `LeftOpen`, `KeepAppsThatAsk`, `SessionState.LeftOpen`.
- Produces:
  - `AppWindows.Of(int pid) : IReadOnlyList<IntPtr>` — the process's visible top-level windows with no owner (what `Process.MainWindowHandle` picks from, all of them).
  - `AppWindows.AskToClose(int pid) : bool` — posts `WM_CLOSE` (0x0010) to each; true when at least one post succeeded.
  - `AppWindows.Responds(int pid) : bool` — some window of `Of(pid)` answers `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 1000 ms)`.
  - `Switcher.CloseAppsAsync` returns only the names really closed.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] // a process with no window, and one that's gone: nothing to ask, nothing asking
public void A_process_without_windows_is_not_asking()
{
    Assert.Empty(AppWindows.Of(Environment.ProcessId));
    Assert.False(AppWindows.AskToClose(Environment.ProcessId));
    Assert.False(AppWindows.Responds(Environment.ProcessId));
    Assert.False(AppWindows.Responds(int.MaxValue - 7));
}
```

- [ ] **Step 2: Run it, expect a compile failure.**
- [ ] **Step 3: Implement `AppWindows`** with `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindow(GW_OWNER = 4)`, `PostMessage`, `SendMessageTimeout` added to `Win32`.
- [ ] **Step 4: Change `Switcher`**
  - `PoliteClose(roots, seconds, keepAsking)` asks with `AppWindows.AskToClose` instead of `CloseMainWindow`, waits exactly as today, and returns the PIDs of asked roots that have not exited and for which `AppWindows.Responds` is true (empty when `keepAsking` is false).
  - `CloseNow(all, plan, politeSeconds, keepAsking)` runs `PoliteClose` first, builds the targets from `KillOrder(all, plan, leftOpen)`, and returns the targets and `SwitchPlanner.LeftOpen(plan, leftOpen)`.
  - `StartGaming` stores the left-open ids in `session.LeftOpen` and logs the exact line from Global Constraints when there are any. `CloseAppsAsync` leaves those names out of its result and logs the same line.
- [ ] **Step 5: Run all unit tests, expect pass.**
- [ ] **Step 6: Commit** — `feat(core): leave an app open when it asks before closing`

### Task 3: Live checks with a stand-in app

**Files:**
- Modify: `tools/live/live-test.ps1`

The stand-in is compiled by the script into the test folder (`Add-Type -OutputAssembly ... -OutputType WindowsApplication`, Windows PowerShell 5.1) as `AsksToSave.exe`, a WinForms app whose first argument picks what it does when asked to close: `ask` (a modal "Save changes?" box, stays open), `inline` (cancels the close, no box), `freeze` (blocks its UI thread), `hide` (hides its window), `two` (two windows, closes normally, exits when both are gone). It is added to the test settings as a custom app with its close toggle on. Clean-up ends only `AsksToSave` processes whose path is inside the test folder.

- [ ] **Step 1: Add the checks** (each: start the stand-in, `--gaming`, wait for `Gaming mode (manual)` in the log, then `--coding`)
  - `ask`: still running; the log has `Left open (asked a question): [AsksToSave]`; `session.json` lists the row in `LeftOpen`; after `--coding`, still exactly one `AsksToSave`.
  - `inline`: still running, logged as left open.
  - `freeze`: gone, not logged as left open.
  - `hide`: gone.
  - `two`: gone, and not logged as left open.
  - `ask` with `KeepAppsThatAsk = false`: gone.
  - Auto mode (`AutoMode = true`, Character Map as the game): `ask` is left open after `Gaming mode (auto)`, and the foreground window does not belong to `AsksToSave`.
- [ ] **Step 2: Build and run** — scratchpad `build.ps1`, then `tools\live\live-test.ps1`; every check `ok`. Note where the question appears on screen.
- [ ] **Step 3: Prove the checks can fail** — run the `ask` and `two` checks once against the 047ab97 exe (`git stash` is not needed: build that commit into a scratch folder) and confirm they fail there.
- [ ] **Step 4: Commit** — `test(live): apps that ask, freeze, hide or have two windows`

### Task 4: Setting, Home line, notification, README

**Files:**
- Modify: `src/VibeShift/MainViewModel.cs`, `src/VibeShift/MainWindow.xaml`, `src/VibeShift/App.xaml.cs`, `README.md`

**Interfaces:**
- Consumes: `Settings.KeepAppsThatAsk`, `SessionState.LeftOpen`.
- Produces: `MainViewModel.KeepAppsThatAsk` (bound toggle, saves on change), `LeftOpenNames : IReadOnlyList<string>` (rows in `session.LeftOpen` that are still running, by name), `LeftOpenText`, `HasLeftOpen`, `LeftOpenTitle` (the notification title). All four are raised after every refresh and switch.

- [ ] **Step 1: View model and XAML** — the toggle goes first in the CLOSING APPS tile, in the `ToggleSwitch` row pattern of "Switch back when my game closes", with a separator before "Force-close after". The Home line is a `Hint` text block under the "Playing ..." row, shown while `HasLeftOpen`. Copy is in Global Constraints.
- [ ] **Step 2: Notification** — in the `Switched` handler: when the mode changed to gaming, the window isn't visible and `LeftOpenNames` isn't empty, `tray.Notify(vm.LeftOpenTitle, <text>)`.
- [ ] **Step 3: Render and look** — `--screenshot` of Settings (`--bottom` if needed) in dark and light; the toggle and both hints read correctly.
- [ ] **Step 4: README** — How it works step 1; the Auto mode paragraph ("save that file first" goes); the "never pop anything up over your game" paragraph gains one sentence: an app's own question may appear, VibeShift shows nothing of its own.
- [ ] **Step 5: Build, unit tests, live checks again; commit** — `feat: tell the user which apps were left open`
