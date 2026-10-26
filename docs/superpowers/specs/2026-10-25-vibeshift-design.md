# VibeShift: design

Date: 2026-10-25. Owner: bth0mp. Status: draft for approval.

## Intent

People who vibe code with AI tools on the same Windows PC they game on leave a lot running in the background: Claude, ChatGPT, Cursor, local models, MCP servers, Docker, WSL. Together these hold gigabytes of RAM and some CPU while a game is running. The owner hit this playing Deadlock: three `desktop-commander` MCP server trees plus a Rocky 9 WSL VM held about 2.5 GB while the game lagged.

VibeShift is a one-click switch between **vibe coding mode** and **gaming mode**. Gaming mode closes the AI and dev apps you picked and shows how much RAM that freed. Switching back reopens the apps it closed. It is a free, public, MIT-licensed app on GitHub, meant for anyone in this situation, not only the owner.

The owner already has a personal PowerShell version (`C:\Users\bth0m\Tools\GameMode.ps1`). VibeShift is a separate, universal app and does not replace or modify that script.

Success: a stranger downloads one `.exe` from GitHub Releases, runs it, sees their AI apps already listed with their RAM use, clicks **Start gaming**, gets their RAM back, and later clicks **Back to vibe coding** and has their apps open again. They never have to edit a config file.

## Goals

1. One-click switch in both directions from a modern dashboard window.
2. Find common AI/dev apps automatically. Let anyone add any other app.
3. Show live RAM per app and how much switching will free. After switching, show how much was actually freed.
4. Reopen exactly what was closed when switching back.
5. Dark and light themes with a purple accent. Follow the Windows theme by default.
6. Optional **Auto mode**: switch to gaming when a game starts, and back when it closes.
7. Ship as a public GitHub repo with a downloadable single-file `.exe`.

## Non-goals

- macOS or Linux.
- FPS "boosters": registry tweaks, GPU or power-plan changes, network tweaks, timer-resolution hacks.
- Restoring in-app state (open chats, tabs, editor files). Reopening the app is the whole job.
- Launching games, or being a game library.
- Code signing (costs money yearly). The SmartScreen warning is documented instead.
- Telemetry, accounts, update checks, or any network access.

## Anti-goals (failure even if it "works")

- **Closing something the user didn't switch on.** Only rows that are switched on get touched.
- **Killing the game, VibeShift itself, or a Windows system process.** Hard block list, enforced in code, not just in the default catalog.
- **VibeShift being heavy.** An app that frees RAM must not eat it. Target: idle under 1% CPU and comfortably under 150 MB working set.
- **Needing admin for normal use.** The app runs unelevated. Only the optional Extra boost asks for admin, once per switch.
- **Losing what to reopen.** If VibeShift crashes or the PC reboots while in gaming mode, **Back to vibe coding** must still reopen the right apps.
- **Pop-ups over the game.** Nothing steals focus while a game is running. No UAC prompt unless the user turned Extra boost on.

## Constraints

- Windows 10 and 11, x64. (An arm64 build is optional, see "Left to the builder".)
- C# on .NET 10 (the SDK on the build machine is 10.0.402), WPF UI.
- Published as a **self-contained single-file** `VibeShift.exe`, so users don't install .NET.
- Runs unelevated (`asInvoker` manifest).
- Settings and state live in `%APPDATA%\VibeShift\`. Nothing is written next to the exe.
- Only one instance runs. Launching it again brings the existing window to the front.

## Decided

Items marked *(my call)* were proposed by Claude and accepted by the owner in conversation without discussion. They can still be revisited.

### Name and identity

- Name: **VibeShift**. Tagline: "One click from vibe coding to gaming, and back."
- Repo: `github.com/bth0mp/VibeShift`, **public**, MIT license *(my call)*.

### Look

The owner picked dashboard layout "B" from three mockups and approved the refined version. The mockup HTML is saved next to this doc at `mockups/home-v2.html` (both themes and both states) and `mockups/layout-options.html` (the three options; B is the chosen one). Treat them as the visual reference. They are not pixel specs.

- Window layout: a narrow icon rail on the left (**Home**, **Apps**, **Settings**) and the page content on the right.
- Home page, top to bottom:
  - Mode header with a status dot: green **Vibe coding mode**, purple **Gaming mode**.
  - Three stat tiles. In vibe coding mode: **FREE RAM**, **AI APPS** (count open), **CAN FREE** (estimate). In gaming mode: **FREE RAM**, **AI APPS**, **FREED** (measured).
  - In vibe coding mode, a list titled **CLOSE WHEN GAMING** with a **+ Add app** link. Each row shows the app icon, name, live RAM and a toggle switch.
  - In gaming mode the list is titled **CLOSED FOR GAMING**. Closed rows are dimmed and tagged "reopens after". The MCP row is tagged "come back when their app restarts" (the mockup's "come back with Claude" was an example). Rows that were left alone say "left running".
  - One primary button: purple gradient **Start gaming** in vibe coding mode, outlined **Back to vibe coding** in gaming mode.
- Palette, from the mockups:
  - Dark: background `#0f0d15`, surface `#1a1724`, raised `#221e2f`, border `#2c2740`, text `#ede9fe`, muted `#9c94b3`, accent `#8b5cf6`, highlight `#c084fc`.
  - Light: background `#f7f5fc`, surface `#ffffff`, raised `#f1edfb`, border `#e4ddf5`, text `#1e1631`, muted `#6b6385`, accent `#7c3aed`.
  - Primary button gradient: `#7c3aed` to `#a855f7`, with a soft purple glow.
  - The accent stays purple whatever the Windows accent colour is.
- Font: Segoe UI Variable, falling back to Segoe UI. Rounded corners (about 8-10 px), Windows 11 feel. Use the Mica backdrop where available (Windows 11). Windows 10 gets a solid background.

### Switching to gaming mode

1. Record free RAM (`GlobalMemoryStatusEx`).
2. For each switched-on row that is running:
   - Record what's needed to reopen it (see "Reopening").
   - Ask it to close politely (send its main window a close message).
   - Wait the grace period (default 5 s, adjustable in Settings), then force-kill its whole process tree.
   - Many Electron apps (Claude, ChatGPT, Cursor, Discord-style apps) only hide to the tray on close, so in practice most will be force-killed after the wait. That's expected.
3. MCP servers row, if on: kill **every** matching MCP process, whatever app is hosting it (see "Detection").
4. Docker / WSL row, if on:
   - If Docker Desktop is running, close it like any other app and record it for reopening.
   - Then, if WSL is running, run `wsl --shutdown`.
5. Extra boost, if on: one elevated helper call stops the listed Windows services (default `SysMain`, `WSearch`). Record which of them were actually running.
6. Measure free RAM again and show the difference as **FREED**.
7. Write the gaming-session state to disk (see "State").

Switching must not block the UI. Show progress on the button, for example "Closing 3 apps...".

### Switching back to vibe coding

- Reopen only the apps VibeShift closed in this session, in the order they were closed.
- MCP servers are not started directly. They return when their host app (for example Claude) reopens.
- WSL is not restarted *(my call)*. It wakes on demand the next time anything uses it.
- Docker Desktop reopens if it was closed, which brings its own WSL backend back up.
- Skip reopening any app that's already running (for example after a reboot, if it launched with Windows).
- Extra boost: start again only the services that were running before.
- Clear the gaming-session state.

### Reopening apps (important)

- For a normal app, reopen by the full exe path recorded at close time.
- **Packaged (MSIX/Store) apps can't be launched from their exe path**, because `C:\Program Files\WindowsApps\...` is locked down. Example: Claude Desktop on the owner's PC lives at `C:\Program Files\WindowsApps\Claude_3.6556.2.0_x64__pzs8sxrjxfjjc\app\Claude.exe`. For these, resolve the package to its AppUserModelID (package family name + app id) and launch with `explorer.exe shell:AppsFolder\<AUMID>`.
- Apps must reopen as normal, unelevated user processes, never elevated.
- If an app fails to reopen, show it on its row ("couldn't reopen") and keep going with the rest.

### The app catalog and detection

A built-in catalog (data, not code paths) of known apps. Each entry: display name, process names, how to tell it's installed, and default toggle state. Detected = installed or currently running. Rows are only shown for detected apps plus apps the user added.

| Catalog entry | Default when detected |
|---|---|
| Claude (desktop) | on |
| ChatGPT (desktop) | on |
| Cursor | on |
| Windsurf | on |
| Microsoft Copilot app | on |
| Ollama | on |
| LM Studio | on |
| MCP servers | on |
| VS Code | off |
| Docker / WSL (one row) | off |
| Command-line AI agents (Claude Code, Codex CLI, Gemini CLI) | off, and never reopened *(my call)* |

- Defaults: AI apps on, heavier dev tooling off *(my call)*.
- **Docker / WSL is one row**, as in the approved mockup *(my call)*. Docker Desktop runs inside the WSL VM, so two rows would fight: `wsl --shutdown` kills Docker's engine anyway, and closing Docker alone leaves the VM's memory (`VmmemWSL`) in use. The row is shown if either is detected. Its RAM is `VmmemWSL` plus Docker Desktop's process tree.
- Command-line agents are a single row, off by default, because killing them loses in-flight work and they can't be reopened into their terminal. Watch for a name clash: Claude Desktop and the Claude Code CLI are both `claude.exe`. Tell them apart by path or parent process.
- **MCP servers**: these are stdio child processes (`node`, `npx`, `python`, `uv`, `uvx`, `bun`, `deno`) of AI host apps. Detect them as either:
  - descendants of a catalog AI app process, or
  - any of those runtimes whose command line matches an MCP pattern (`mcp`, `modelcontextprotocol`, `desktop-commander`, and similar). This catches orphans.
  - Never match unrelated `node`/`python` processes. The owner runs a Python web app (`pythonw -m coinwatch ...`) that must survive.
  - When the MCP row is on, kill all of them, including ones whose host app stays running (for example a command-line agent row that's off). The owner asked to "turn off any MCP thing", and that's what the row promises. A host left running will have broken tools until it reconnects. That's accepted, and the row's tooltip says so. So MCP processes don't need to be assigned to a host.
- **+ Add app**: the user picks an `.exe` (or a running process). It becomes a row with its own toggle and can be removed. Custom rows default to on.
- **Block list** (refuse to add, never close): VibeShift itself, Windows system processes (`csrss`, `wininit`, `winlogon`, `services`, `lsass`, `smss`, `svchost`, `dwm`, `explorer`, `sihost`, `fontdrvhost`, `RuntimeBroker`, `ctfmon`, `audiodg`, `MsMpEng`, and similar), anything from `C:\Windows\`, and the currently detected game.
- Per-row RAM: sum of private working set across the app's process tree (this matches Task Manager's Memory column). Refresh about every 2-3 s while the window is visible. Refresh less often, or not at all, while hidden in the tray.
- **CAN FREE** = sum of RAM for switched-on rows that are running.

### Auto mode

- Off by default. A switch on the Settings page.
- When on, VibeShift watches for a game process starting and runs **Start gaming** by itself. When the last game process exits, it waits about 10 s (in case the launcher restarts the game) and then runs **Back to vibe coding** *(my call)*.
- **What counts as a game** *(my call)*: a process whose exe lives in a known game library folder, or one on the user's own list.
  - Steam: parse `libraryfolders.vdf` (Steam path from `HKCU\Software\Valve\Steam`), then each library's `steamapps\common`.
  - Epic: manifests in `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item`, field `InstallLocation`.
  - GOG: registry `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\*`, value `path`.
  - EA app and Xbox app install folders.
  - Plus a **My games** list in Settings (pick an `.exe`).
- Exclude launchers, helpers, crash reporters, anti-cheat services and installers even when they sit inside a library folder, for example `steam`, `steamwebhelper`, `EpicGamesLauncher`, `UnityCrashHandler*`, `CrashReport*`, `EasyAntiCheat*`, `BEService`, `vcredist*`, `DXSETUP`, `*Setup*`, `*Installer*`.
- Also exclude Steam apps that aren't games, above all **Wallpaper Engine** (`wallpaper32`, `wallpaper64`, `steamapps\common\wallpaper_engine`). It runs all day, and the owner has it, so it would otherwise keep Auto mode stuck in gaming. The builder should find a general way to filter Steam tools and software, not just a name list, if one exists.
- **Automatic switches skip anything that can pop up over the game** *(my call)*:
  - Extra boost (its UAC prompt) is skipped on automatic switches and only runs on manual ones.
  - Automatic switches force-close straight away, without the polite close and grace period, so "Save changes?" or "Quit?" dialogs can't appear over the game. VS Code-family editors like Cursor keep unsaved edits in backups, so a force-close usually doesn't lose them.
- **Manual override wins.** If the user clicks **Back to vibe coding** while a game is still running, Auto mode doesn't switch to gaming again until that game exits and a new one starts. If the user starts gaming manually, Auto mode doesn't switch back on game exit.
- Auto switches never steal focus or show a window. The tray icon and its tooltip reflect the change.

### Tray, startup, window behaviour

- Tray icon (by the clock) is always present while running *(my call)*:
  - Left-click opens the window.
  - Right-click menu: **Start gaming** or **Back to vibe coding**, **Open VibeShift**, **Quit**.
  - The icon or tooltip shows the current mode.
- Closing the window hides it to the tray. **Quit** from the tray menu exits.
- **Start with Windows**: off by default. Uses the `HKCU\...\Run` registry key and starts minimized to the tray.

### Settings page

- **Theme**: Follow Windows (default), Dark, Light. Changes apply live.
- **Auto mode**: on/off, plus the **My games** list.
- **Start with Windows**: on/off.
- **Force-close after**: seconds, default 5.
- **Extra boost**: off by default. Explains that it needs an admin prompt and only runs when you click the button, not on Auto mode switches. When on, shows the service list (default `SysMain`, `WSearch`).
- **About**: version, GitHub link, license.

### Apps page

The full catalog view: every detected app and every custom app with its toggle, plus **+ Add app** and remove for custom rows. The Home list is the compact version of this.

### State and settings

- `%APPDATA%\VibeShift\settings.json`: theme, toggles per app, custom apps, My games, Auto mode, Start with Windows, grace period, Extra boost and its services.
- `%APPDATA%\VibeShift\session.json`: present only while in gaming mode. Holds the closed apps and how to reopen each, services stopped, the before/after RAM numbers, and whether the switch was manual or automatic. On startup, if it exists, VibeShift opens in gaming mode so **Back to vibe coding** still works after a crash or reboot.
- `%APPDATA%\VibeShift\vibeshift.log`: a short rolling text log of switches and failures, for GitHub issue reports *(my call)*.

### Shipping

- Public repo `bth0mp/VibeShift` with MIT `LICENSE`, `.gitignore`, and a README containing:
  - what it is and why (vibe coding to gaming),
  - screenshots of the dashboard in dark and light,
  - Download: link to Releases, plus how to get past "Windows protected your PC" (**More info**, then **Run anyway**) and why it appears (unsigned),
  - what gets closed and reopened, what counts as a game in Auto mode, and that nothing leaves the PC,
  - building from source (`dotnet publish` command).
- Release `v1.0.0` on GitHub Releases with `VibeShift.exe` (win-x64, self-contained single file), built locally and uploaded with `gh` *(my call: no CI for v1)*.
- The project lives locally at `D:\Claude Code\VibeShift` *(my call)*.

## Left to the builder

- WPF control library. A Fluent library such as WPF-UI is fine, and so are hand-rolled styles, as long as it matches the mockups and stays light.
- How the tray icon is implemented (WinForms `NotifyIcon` avoids an extra dependency).
- How process command lines are read (WMI `Win32_Process` or native), and how game launches are watched (polling or WMI events). Must work unelevated and stay under the CPU target.
- Exact catalog entries: process names and install-detection paths per app.
- Window size, icons, animation, the app/tray icon artwork (purple, in keeping with the mockup logo square).
- An arm64 build alongside x64 if it's cheap.
- Project structure and test approach. The non-UI logic (catalog matching, MCP matching, game-folder matching and exclusions, block list, session state round-trip, reopen-target resolution) needs automated tests. The UI doesn't.
