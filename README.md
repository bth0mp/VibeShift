<p align="center"><img src="assets/icon.png" width="72" alt="VibeShift icon"></p>

<h1 align="center">VibeShift</h1>

<p align="center"><b>One click from vibe coding to gaming, and back.</b></p>

<p align="center">
  <img src="docs/screenshots/dark-coding.png" width="360" alt="VibeShift in dark mode, ready to start gaming">
  &nbsp;
  <img src="docs/screenshots/light-gaming.png" width="360" alt="VibeShift in light mode, in gaming mode">
</p>

If you build with AI tools on the same Windows PC you game on, you've probably had this happen: you launch a game and it stutters. Claude, ChatGPT, Cursor, a local model, a handful of MCP servers and maybe Docker or WSL are still running in the background, holding several gigabytes of RAM between them.

VibeShift closes the ones you choose in one click and shows how much memory you got back. When you're done playing, one more click reopens everything it closed.

## Download

1. Download **VibeShift.exe** from the [latest release](https://github.com/bth0mp/VibeShift/releases/latest).
2. Run it. There's no installer, and you don't need to install anything else. Windows 10 or 11, 64-bit.

The first time you run it, Windows may show **"Windows protected your PC"**. Click **More info**, then **Run anyway**. This appears because the app isn't code-signed, which costs money every year. All the source code is in this repo, and you can build the exe yourself (see [Build from source](#build-from-source)).

## How it works

**Start gaming**

1. Asks each app you've switched on to close. Anything still open after 5 seconds (you can change this) is force-closed.
2. Stops leftover MCP servers.
3. Shuts down WSL and Docker, if you've switched them on.
4. Pauses a couple of Windows background services, if you've turned on [Extra boost](#extra-boost).
5. Shows how much RAM was freed.

**Back to vibe coding**

- Reopens the apps VibeShift closed, in the order it closed them, plus your **vibe apps**: anything you've switched to **Open** on the Apps page (Claude and ChatGPT, say) opens every time you come back, even if it wasn't open before. Apps that are already running are skipped.
- **Switch back when my game closes** (on by default) does this for you about 10 seconds after your game exits, even if you clicked Start gaming yourself. A Windows notification tells you what opened.
- Microsoft Store apps (like Claude and ChatGPT) reopen through Windows, the same way the Start menu opens them.
- If VibeShift crashes or your PC restarts in the middle of a game, it opens in gaming mode next time, so **Back to vibe coding** still knows what to reopen.
- Still in gaming mode 2 hours after your last game (the setting is off, say, or you played something VibeShift doesn't recognise)? A Windows notification reminds you, once. It waits until nothing is running full screen.

**Play button**

- **▶ Play Deadlock** (or whichever game you played last) sits under Start gaming: one click closes your apps for gaming, opens your game-time apps, then launches the game through Steam or Epic. Pick a different game under **Settings → Play button**; your own games from **My games** are listed too.
- With **Switch back when my game closes** on, that's the whole loop: one click to play, and your AI apps are back when you quit.

**Game-time apps**

- The Apps page has three switches per app: **Close** (closed when you start gaming), **Game** (opened when you start gaming, like Discord, OBS or Spotify) and **Vibe** (opened when you're back to vibe coding). An app can't be both closed and opened for gaming, so Game and Close turn each other off.

**Keyboard shortcuts** (work from anywhere, even mid-game; turn them off in Settings)

| Shortcut | Does |
|---|---|
| **Ctrl+Alt+G** | Start gaming / back to vibe coding |
| **Ctrl+Alt+V** | Open all AI apps |
| **Ctrl+Alt+P** | Play your game |

If another app already uses one of these, Settings says so.

**Vibe page (quick on/off)**

- The ⚡ page lists your AI apps (Claude, ChatGPT, Cursor, Windsurf, Copilot, Ollama, LM Studio, plus anything you add) with a live switch each: on opens the app right now, off closes it right now. **Open all** and **Close all** do the whole list at once.
- It doesn't switch modes or touch MCP servers, WSL or services; it's for quickly starting or stopping your tools while you work.
- The same controls are in the tray's right-click menu: **Open all AI apps**, **Close all AI apps**, and an **AI apps** submenu where a ✓ means running. Click an app to open or close it, without opening the window.
- VibeShift learns how to start an app the first time it sees it running, so an app shows up on the Vibe page once you've opened it once (or added it yourself).

**Power plan**

- Under **Settings → Power plan**, pick a plan to use while you game: Windows' High performance, or your PC maker's own (a Lenovo Legion's Performance Mode, say). VibeShift switches to it when you start gaming and back to the plan you had when you're done, even after a crash. Switching plans needs no admin permission.

**Stats**

- Home shows how long you've been in gaming mode, and in vibe coding mode, your last 7 days: sessions, RAM freed and time spent gaming.

**Updates**

- **Settings → About → Check for updates** asks GitHub for the latest release. If there's a newer one, one click downloads it, checks it against the SHA-256 GitHub publishes for it, swaps it in and restarts VibeShift. Your settings and any gaming session carry over.
- Turn on **Check for updates automatically** to have VibeShift check once a day. It's off until you turn it on, and VibeShift never updates without your click.

Closing the window keeps VibeShift running as a small icon by the clock, with a green dot in vibe coding mode and a purple one while gaming. Right-click it to switch modes or quit. Windows 11 tucks new icons under the **^** arrow; drag it onto the taskbar to keep it in sight, and turn on **Start with Windows** in Settings so it's always there.

## What it can close

| App | Switched on by default | Reopened afterwards |
|---|---|---|
| Claude (desktop) | Yes | Yes |
| ChatGPT (desktop) | Yes | Yes |
| Cursor | Yes | Yes |
| Windsurf | Yes | Yes |
| Microsoft Copilot | Yes | Yes |
| Microsoft 365 Copilot | Yes | Yes |
| Ollama | Yes | Yes |
| LM Studio | Yes | Yes |
| MCP servers | Yes | They come back when their app restarts |
| VS Code | No | Yes |
| Docker / WSL | No | Docker Desktop yes. WSL starts again by itself the next time something uses it |
| Command-line AI agents (Claude Code, Codex CLI, Gemini CLI, including npm installs) | No | No. Closing one ends what it was doing, and it can't be reopened inside your terminal |
| Web browsers (Firefox, Chrome, Edge, Brave, Opera, Vivaldi) | No | Yes, with your tabs (see below) |
| Wallpaper Engine | No | Yes |
| PowerToys | No | Yes |
| Windows Widgets | No | Windows starts them again when needed |

- **When apps show up.** An app appears in VibeShift once it has seen it running. Use **+ Add app** to add any other program.
- **MCP servers.** VibeShift spots these by their command line (`mcp`, `modelcontextprotocol`, `desktop-commander`). Other Node or Python processes, such as language servers, dev servers and your own scripts, are left alone. With the MCP row switched on, every MCP server stops, including ones used by apps you keep open. Those apps get their tools back when they restart.
- **Using the most RAM.** The Apps page lists other apps using a lot of memory right now, each with an **+ Add** button. Windows itself, games and game launchers like Steam are never listed.
- **Browsers and your tabs.** The Web browsers row ends the browser rather than asking it to close, because that's what makes it restore every tab when it reopens. Want to keep a music tab playing while you game? Leave this row off and let the browser sleep idle tabs instead: Firefox does this on its own when memory runs low, Chrome has **Memory Saver**, Edge has **Sleeping tabs**, and the free **Auto Tab Discard** extension lets you choose exactly what stays awake.
- **What it never closes.** Windows' own processes, the game you're playing (or anything it started), game stores like Steam, Epic or Battle.net (even when an app you close started them), and VibeShift itself.
- **Separate apps stay open.** A browser opened from a link in Claude, or an app whose row you switched off, survives even if a closed app started it.
- **Started VibeShift from an app's terminal?** If that's an app VibeShift closes (Claude Code, an AI app's built-in terminal), VibeShift restarts itself through Windows first. Closing the app for gaming then can't take VibeShift down with it. If it can't move out (it was started with `--tray`, say), that app is left running instead, since VibeShift can't close part of it and reopen it afterwards.

## Auto mode

Off by default. Turn it on in Settings and VibeShift switches to gaming mode by itself when a game starts. It switches back 10 seconds after the game closes, so a launcher restarting the game doesn't bounce your apps.

**What counts as a game:**
- Any program with a window running from a Steam, Epic, GOG, EA app or Xbox app games folder. VibeShift finds those folders automatically.
- Anything you add under **My games**.
- Launchers, crash reporters, anti-cheat services, installers and non-game Steam apps such as Wallpaper Engine are ignored.
- While you're in gaming mode, Home shows what VibeShift sees you playing (**Playing Deadlock**). If that's not a game (an idle game or desktop pet you leave running all day), click **Not a game** and VibeShift ignores it from then on. They're listed under **Not games** in Settings, where you can undo it.

**Your clicks always win:**
- Switch back to vibe coding by hand while a game is still running, and Auto mode leaves that game alone.
- Start gaming by hand, and Auto mode won't switch you back.

Automatic switches never open the VibeShift window or pop anything up over your game.

## Extra boost

Off by default. When it's on, **Start gaming** also pauses two Windows background services: SysMain (memory prefetching) and Windows Search indexing. **Back to vibe coding** restarts them. Pausing and restarting services needs admin permission, so Windows asks when you click Start gaming and again when you switch back (after your apps have reopened, so they never wait on the prompt). Auto mode skips this step so nothing pops up mid-game.

## Privacy and permissions

- VibeShift only goes online to check GitHub for updates: when you click **Check for updates**, or once a day if you turn that on. No telemetry, no accounts.
- It runs as a normal app, without admin rights. Only Extra boost asks for admin, and only when you click.
- Its files live in `%APPDATA%\VibeShift`:
  - `settings.json`: your choices.
  - `session.json`: what to reopen. It only exists while you're in gaming mode.
  - `stats.json`: when your last 90 days of gaming sessions started and ended, and the RAM each one freed.
  - `vibeshift.log`: a short log of switches and the games VibeShift saw start and close, handy if you open an issue.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
dotnet publish src/VibeShift -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The exe ends up in `publish\VibeShift.exe`. It isn't compressed on purpose: a compressed single-file build is half the download but uses about twice the RAM, and saving RAM is the point.

Every push to `main` is built and tested on GitHub Actions, and each run's exe can be downloaded from its Actions page for 14 days.

## License

[MIT](LICENSE)
