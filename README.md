# AceBot Gaming Mode

One click pushes everything that isn't gaming, streaming or voice chat to the back. Click again and it's all put back.

## What it does while on

- **Background apps** get the lowest CPU priority plus Windows Efficiency mode, and hand back RAM they aren't using. The app you're using always runs at full speed; anything fullscreen is treated as a game.
- **Pauses** Windows Search indexing, SysMain, telemetry, Windows Update (expires on its own) and Automatic Maintenance.
- **Caps uploads** of cloud sync, browsers and torrent apps so your stream keeps the bandwidth.
- **Limits Defender's scheduled scans** to 5% CPU (real-time protection untouched).
- **Turns off Game Bar background recording** while OBS / Streamlabs / XSplit are open.
- Switches to High performance (only from Balanced / Power saver) and makes sure Windows Game Mode is on.
- When it turns off, a notification says how much CPU the pushed-back apps used.

Every part can be switched off in **Gaming Mode Settings**. Nothing is ever closed or killed.

## How it's built

`GamingMode.exe` is both:

- the **`AceBotGamingMode` service** (`--service`, LocalSystem): does all admin work, so there are no UAC prompts. It writes what it changed to `%ProgramData%\AceBot\GamingMode\state.json` as it goes, and on startup puts back anything left over from a crash or restart. Stopping or uninstalling the service turns Gaming Mode off first.
- the **tray app** (normal user): icon, notifications, settings window, and tells the service which window you're using.

They talk over the local named pipe `AceBot.GamingMode` (network access denied; every request acts on the caller's own session, read from Windows).

| Path | |
|---|---|
| `src/GamingMode.Core` | engine: throttler, system tweaks, settings, state, pipe protocol |
| `src/GamingMode.App` | `GamingMode.exe`: service + tray + settings window |
| `tests/GamingMode.Tests` | unit tests |
| `installer/GamingMode.wxs` | per-machine WiX 5 MSI |
| `legacy/` | the original PowerShell version (no longer installed) |

## Build

```
powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Version 1.0.1 -Installer
```

Runs the tests, publishes a self-contained single-file `artifacts\publish\GamingMode.exe`, and builds `artifacts\GamingMode-<version>-x64.msi`. Bump the version for every MSI you hand out, or Windows won't upgrade over the old one.

Command line: `--toggle`, `--settings`, `--console` (run the service in a window for debugging, as admin), `--close-tray`.

## Releases and automatic updates

```
git tag v1.2.0
git push origin v1.2.0
```

The **Release** workflow runs the tests, builds the MSI and publishes it with a `.sha256` file as a GitHub Release. Installed copies check `github.com/CodeBugd/gaming-mode/releases/latest` a couple of minutes after startup and every 12 hours (or right away via the tray's **Check for updates**). An update is only installed while Gaming Mode is off; the MSI is downloaded into SYSTEM's own temp folder, must come from this repo's releases over HTTPS and match its checksum, and is installed silently. The service then brings the tray icon back and it shows "Gaming Mode updated". Turn it off in Settings → Features.

The version comes from the tag; `Directory.Build.props` only sets the version of local builds.

## Not done yet

- **Code signing.** Unsigned, so Windows SmartScreen warns when someone downloads the MSI, and antivirus may be suspicious of a service that changes other processes. Azure Trusted Signing is the cheapest legit option. It also matters for updates: today the checksum only proves the download wasn't corrupted, so anyone who gets into this GitHub account could ship an update. Once releases are signed, the updater should also require the signature.
- Only tested on one PC (Windows 11, Intel, NVIDIA).
