# Security

VibeShift closes and starts processes on your PC, and its optional updater downloads and replaces its own exe, so security reports are welcome.

## Reporting a problem

Please don't open a public issue. Use GitHub's private reporting instead: the **Security** tab of this repo, then **Report a vulnerability**. Include the VibeShift version (Settings, About), what an attacker could do, and how to reproduce it.

## Supported versions

Only the [latest release](https://github.com/bth0mp/VibeShift/releases/latest) gets fixes. Settings carry over when you update.

## What VibeShift already guards against

- The updater only downloads from this repo's releases, checks the file against the SHA-256 GitHub publishes for it, and never installs an older version.
- Windows' own programs (Explorer, powercfg, wsl) are started by their full path, never looked up in the current folder.
- It never closes Windows' own processes, other users' processes, or the game you're playing.
