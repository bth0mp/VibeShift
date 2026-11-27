# Protect unsaved work: design

Date: 2026-11-27. Owner: bth0mp. Status: draft for approval.

## Intent

Start gaming asks each app to close, waits 5 seconds, then force-closes whatever is left. An app that answered with a question ("Save changes?") is force-closed with the question still on screen, and the work is lost. Auto mode is worse: it never asks, and force-closes straight after its 10-second notice, which Windows holds back while a game is full screen.

An app that asks a question instead of closing should stay open, and VibeShift should say which app it left.

## Goals

1. An app that answers the close request with a question is left open, with everything it started, in every way of switching: the button, the shortcut, the command line, Auto mode, and the Vibe page's Close switch.
2. The user is told which apps were left open.
3. Everything else closes exactly as it does today.

## Non-goals

- Deciding per app. One setting covers every app.
- A "close anyway" button inside VibeShift. The app's own question is on screen; answering it is the way.
- Guessing whether an app has unsaved work without asking it.
- Browsers and command-line agents. Browsers are still ended outright so their tabs restore, and a command-line agent has no window to ask.

## Anti-goals (failure even if it "works")

- An app that closes fine today (Claude, ChatGPT, Cursor, VS Code, Docker Desktop) stops being closed because VibeShift wrongly thinks it is asking.
- VibeShift brings a window in front of a game.
- A frozen app is left running because it "still has a window".

## Constraints

- An app's question may be a real dialog box or drawn inside the app's own window (Blender, Godot, newer Windows apps). Windows gives no common signal for "this app is asking".
- `Process.CloseMainWindow` asks one window only. An app with two windows keeps one open today until the force-close.
- The user games while sessions run: live checks refuse to run while a game is running, and never touch the user's own VibeShift or apps.

## Approaches

1. **Still showing a window (picked).** Ask every window of the app to close. When the wait ends, an app that is still running, still shows a window and still responds is left open. Catches every kind of question, and an app still busy saving. Cost: an app that always asks "Are you sure you want to exit?" stays open every time, so one setting restores today's behaviour.
2. **Dialog box detected.** Leave an app open only when Windows shows a real dialog box over its window. Fewer wrong "left open" results, but it misses questions drawn inside the window, so those apps would still lose work. Rejected: it fails the goal for a known set of apps.
3. **VibeShift asks.** Pause the switch and show VibeShift's own prompt (Close anyway / Leave open), as Windows does at shutdown. Rejected: it can't work in Auto mode, where nobody is there and nothing may appear over a game.

The owner chose that Auto mode asks apps to close as well (2026-11-27). The rest are the builder's calls.

## The design

**Closing.** For each app that is asked politely (every row except browsers), post a close request to every visible top-level window of its root processes. Wait up to "Force-close after" seconds, ending early once every asked app has exited or hidden its windows, as today. Then:

- still running, still showing a window, still responding: **left open**, together with every process under it (its helpers, MCP servers it started);
- anything else is force-closed as today: apps that hid to the tray, frozen apps, processes with no window.

Auto mode does the same. Its switch can take up to "Force-close after" seconds longer when an app asks.

**Setting.** Settings, Closing apps: "Leave an app open when it asks a question", on by default (`Settings.KeepAppsThatAsk`). Off restores today's behaviour exactly, including Auto mode closing apps right away.

**Telling the user.**
- Log: `Left open (asked a question): [Visual Studio]`.
- Home, while in gaming mode and the app is still running: "Still open: Visual Studio. It asked a question instead of closing."
- A Windows notification when the window is out of sight (shortcut, command line, Auto mode). Windows holds it back while a game is full screen, like the 10-second notice.
- Vibe page: the app's switch goes back to on.

**Bookkeeping.** The session records which rows were left open (`SessionState.LeftOpen`, entry ids). A left-open app stays on the reopen list: if the user answers the question and it closes, switching back reopens it; if it is still running, it is skipped as already open. FREED counts only what really closed (it already does).

**Docs.** README: How it works step 1, the Auto mode paragraph, and the "nothing pops up over your game" paragraph (an app's own question may appear; VibeShift shows nothing). Settings hint text.

## Testing

- Unit: the kill list leaves out everything under a left-open root; the plan asks politely on automatic switches when the setting is on, and not when it is off.
- Live, with a small stand-in app that can ask on close in a dialog box, ask inside its window, freeze, or hide: left open in a manual and an automatic switch; force-closed with the setting off; a frozen one force-closed; a two-window app with no question fully closed; the question does not take the foreground from another window during an automatic switch.

## Left to the builder

- Where the window code lives and how the stand-in app is built.
- Whether a click on Start gaming should let the app's question come to the front (`AllowSetForegroundWindow`), decided by what the live check shows.
- `ponytail:` an app that minimizes instead of closing reads as asking; the setting is the way out until someone needs better.
