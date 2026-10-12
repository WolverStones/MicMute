# MicMute

[![GitHub Release](https://img.shields.io/github/v/release/itsnateai/MicMute)](https://github.com/itsnateai/MicMute/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](https://github.com/itsnateai/MicMute)
[![GitHub Downloads](https://img.shields.io/github/downloads/itsnateai/MicMute/total)](https://github.com/itsnateai/MicMute/releases)

Global hotkey microphone mute/unmute with push to talk for Windows.

A lightweight system tray utility that lets you mute and unmute your microphone from anywhere using a hotkey or tray icon click. Works at the Windows audio level — affects all apps at once (Zoom, Discord, Teams, etc.).

## Features

- **Global hotkey**: `Win + Shift + Ctrl + A` (configurable) toggles mic mute system-wide
- **Push-to-Talk mode**: Hold key to unmute, release to re-mute. Fullscreen-safe and accepts bare modifier keys (Right-Ctrl alone, etc.) the way Discord does — no keyboard hook, nothing for game anti-cheat to flag.
- **Sticky PTT**: Left-click the tray in Push-to-Talk mode to hold the mic open without holding the hotkey. A persistent "mic listening" bubble stays on screen so you can't forget. Click again to resume normal PTT.
- **Deafen mode**: Mute both mic and speakers simultaneously (separate hotkey)
- **Tray icon**: Green = active, Red = muted. Left-click to toggle.
- **On-screen display**: Floating dark bubble above the taskbar shows mute state
- **Mute Lock**: Reverts external mute changes on the next 15-second sync tick. Catches drive-by changes from meeting apps or OS sound settings. Not instant — apps that actively manage mute mid-call (Discord PTT, etc.) will win in the moment.
- **Mic source selection**: Pick which microphone to control
- **Sound feedback**: Audible tone on mute/unmute (custom .wav support)
- **Custom icons**: Replace default tray icons with your own .ico files
- **Run at startup**: One-click toggle via Settings
- **Startup state control**: Start muted, unmuted, or remember last session
- **Explorer restart recovery**: Tray icon survives Explorer crashes
- **Auto-detect**: Automatically reconnects when you plug in a new mic

## Screenshots

| Active (Unmuted) | Muted | Tray Menu | Settings |
|:---:|:---:|:---:|:---:|
| ![Active](screenshots/micicon1.png) | ![Muted](screenshots/micicon2.png) | ![Menu](screenshots/micmutemenu.png) | ![Settings](screenshots/micmutesettings.png) |

## Requirements

- Windows 10/11

## Installation

### Option 1: Download

Grab **[MicMute.exe](https://github.com/itsnateai/MicMute/releases/latest)** from the latest release — single file, self-contained, no .NET runtime needed.

### Option 2: WinGet (recommended)

```powershell
winget install itsnateai.MicMute
winget upgrade itsnateai.MicMute   # later, to update
```

WinGet installs stay current automatically. The in-app **Update** button detects WinGet installs and points you back at `winget upgrade` instead of trying to overwrite the managed binary.

### Option 3: Build from source

```bash
git clone https://github.com/itsnateai/MicMute.git
cd MicMute

# Framework-dependent (~280KB, requires .NET 8 runtime)
dotnet publish -c Release -r win-x64

# Self-contained single-file (~147MB, no runtime needed) — matches the release exe
dotnet publish -c Release --self-contained true -r win-x64 -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/MicMute.exe`

### Self-update integrity

Releases publish a `SHA256SUMS` file alongside the exe. The in-app **Update** button downloads it, verifies the hash, and fails closed if anything is missing or doesn't match. Unverified updates never land on disk.

## Usage

### Modes

**Toggle** (default): Press the hotkey to mute, press again to unmute. Left-click the tray for the same effect.

**Push-to-Talk**: Hold the hotkey to unmute. Release to re-mute. Switch via tray menu or middle-click the icon. Push-to-Talk always starts muted at launch.

**Sticky PTT**: In Push-to-Talk mode, left-click the tray icon to unmute and pause the hotkey. A persistent indicator stays on screen until you left-click again to re-mute. Useful for holding the mic open during a long conversation without holding a key.

**Deafen**: Assign a separate hotkey in Settings. Mutes both mic and speakers. Press again to restore both.

### Status Indicator

A small red dot stays on screen while the mic is muted — on the desktop and above games running borderless or in Windows' optimised fullscreen. It is click-through and never steals focus.

- **Move it**: press `Win+Ctrl+Shift+I` (or *Tray → Status Indicator → Move dot…*), drag the dot, then press the hotkey again or right-click the dot to save.
- **Per-game positions**: the position is saved for whichever app was in the foreground when you started moving it, so each game can keep the dot clear of its own HUD. The dot only moves when you switch to an app with its own saved position — clicking the desktop or another app leaves it where it is. The first saved position is also the startup default (move the dot while the desktop is focused to change it).
- **Show while live**: *Tray → Status Indicator → Also show green dot while live* keeps a green dot visible when the mic is open.
- Games in true *exclusive* fullscreen (mostly older DX9 titles) draw over every window — use borderless mode there.

### Tray Menu

Right-click the tray icon for the full menu:
- Toggle mute
- Current hotkey combo (click to change — combo shorthand: `#` Win, `^` Ctrl, `!` Alt, `+` Shift)
- Switch between Toggle and Push-to-Talk modes
- Select mic source
- Open Settings, Help, Sound Settings
- Reinitialise mic (if device changed)
- Status indicator: enable, show while live, move, reset position for the current app

## Customization

Settings are stored in `MicMute.ini` (auto-created next to the exe):

```ini
[General]
Hotkey=#^+a
SoundFeedback=0
Mode=toggle
OSD_Enabled=0
OSD_Duration=800
MuteLock=0
MiddleClickToggle=1
StartMuted=no
DeafenHotkey=
Indicator_Enabled=1
Indicator_ShowWhenLive=0
Indicator_Size=18
Indicator_EditHotkey=#^+i

[IndicatorPositions]
*=6,540
cs2.exe=1880,120
```

| Key | Default | Description |
|-----|---------|-------------|
| `Hotkey` | `#^+a` | Main mute hotkey (`#`=Win, `^`=Ctrl, `!`=Alt, `+`=Shift) |
| `SoundFeedback` | `0` | Play tone on mute/unmute |
| `Mode` | `toggle` | `toggle` or `push-to-talk` |
| `OSD_Enabled` | `0` | Show on-screen mute indicator |
| `OSD_Duration` | `800` | OSD display time in ms |
| `MuteLock` | `0` | Revert external mute changes on the 15s sync tick |
| `MiddleClickToggle` | `1` | Middle-click tray to switch modes |
| `StartMuted` | `no` | `no`, `yes`, `unmuted`, or `last` |
| `DeafenHotkey` | *(empty)* | Hotkey for deafen mode |
| `DeviceId` | *(empty)* | Specific mic device (empty = system default) |
| `IconMuted` / `IconActive` | *(empty)* | Custom .ico file paths |
| `MuteSound` / `UnmuteSound` | *(empty)* | Custom .wav file paths |
| `Indicator_Enabled` | `1` | Show the always-on status dot while muted |
| `Indicator_ShowWhenLive` | `0` | Also show the dot (green) while the mic is live |
| `Indicator_Size` | `18` | Dot diameter in logical pixels (6–64) |
| `Indicator_EditHotkey` | `#^+i` | Hotkey to move the dot / save its position (empty = tray menu only) |

`[IndicatorPositions]` holds the dot's screen position per executable (`game.exe=x,y`); `*` is the default for every other app. Written automatically when you move the dot.

## How It Works

MicMute uses the Windows Core Audio COM APIs (`IAudioEndpointVolume`) to control microphone mute state directly — no dependencies on any specific app. Global hotkeys are registered via `RegisterHotKey` and the tray icon is built using WinForms `NotifyIcon`.

A 15-second periodic sync timer detects external mute changes, device hot-plug events, and enforces Mute Lock when enabled.

## Project Structure

| Path | Description |
|------|-------------|
| `MicMute.csproj` | .NET 8 project file |
| `Program.cs` | Entry point — single-instance enforcement |
| `TrayApp.cs` | Main app — tray icon, hotkeys, mute logic, menus |
| `AudioManager.cs` | Core Audio COM interop — mute, enumerate, speaker control |
| `Config.cs` | INI config reader/writer with hotkey parsing |
| `OsdForm.cs` | On-screen display overlay (click-through, auto-dismiss) |
| `IndicatorForm.cs` | Always-on status dot (click-through, per-game positions) |
| `SettingsDialog.cs` | Settings GUI — includes inline hotkey capture |
| `HelpWindow.cs` | Help text window |
| `NativeMethods.cs` | Win32 P/Invoke declarations |
| `ShortcutHelper.cs` | Windows .lnk shortcut creation for startup |
| `mic_on.ico` / `mic_off.ico` | Tray icons (embedded as resources) |

## Supporting This Project

This app is free and open source. If it saves you time, consider supporting continued development:

<p>
  <a href="https://buymeacoffee.com/itsnate"><img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"></a>
</p>

- **[Buy Me a Coffee](https://buymeacoffee.com/itsnate)** — one-time support

You can also build from source for free — see the build instructions above.

---

## License

[MIT](LICENSE)
