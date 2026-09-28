# Fortnite AFK XP Tool

A small cross-platform (.NET 10 + Avalonia) utility for AFK XP sessions on **GeForce NOW**, **GeForce Infinity**, **Amazon Luna**, **Xbox Cloud Gaming** and the native **Fortnite** client. Runs on Windows and Linux.

## Features

- **Input modes**: mouse click (left/right/middle) or any keystroke, each in
  - **Auto** - repeats at a configurable interval, or
  - **Hold** - holds the button/key down continuously.
- **Camera nudge**: every ~5 minutes (±10%, capped at 6:30) the camera moves a little and returns. Each nudge varies in axis, direction, distance, speed, curve and dwell time (net movement is always zero), so the 7-minute AFK XP timeout never trips.
- **Real mouse movement resets the timers**: if you move the camera yourself while a game window is focused, the countdown restarts. The tool's own synthetic movement is ignored.
- **Focus-aware**: input is only sent while Fortnite / GeForce NOW / GeForce Infinity / Amazon Luna / Xbox Cloud (or a browser tab titled GeForce NOW / Xbox / Fortnite / Luna) is focused. Alt-tab away and input pauses automatically (held keys are released); return and it resumes, no toggling needed.
- **Countdown**: driven by the start/stop toggle, not the active window. Toggling off freezes it, toggling on resumes it. While the clicker is on, the countdown keeps running even if you're tabbed out.
- **Global hotkey** (default `F6`) to start/stop from inside the game. Click the hotkey box to rebind.

## Windows

Download the `win-x64` zip from Releases and run the exe. If the game or streaming client runs as administrator, run this tool as administrator too.

## Linux

Download the `linux-x64` archive from Releases. Input is sent through a virtual `/dev/uinput` device and real mouse/keyboard events are read from `/dev/input`, so it works on Wayland and X11 (same approach as [CachyAutoClicker](https://github.com/Glitxhhh/CachyAutoClicker)). One-time setup:

```bash
sudo usermod -aG input "$USER"
echo uinput | sudo tee /etc/modules-load.d/uinput.conf
sudo cp 99-fortnite-afk-xp.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo modprobe uinput && sudo udevadm trigger
# log out and back in so the group change applies
```

Focus detection depends on your desktop (Wayland has no generic way to ask):

| Desktop | Needs |
| --- | --- |
| Hyprland | `hyprctl` (included) |
| Sway | `swaymsg` (included) |
| KDE Plasma (Wayland) | [`kdotool`](https://github.com/jinliu/kdotool) |
| Any X11 session | `xdotool` |
| GNOME (Wayland) | not supported - turn off "Only send input while the game is focused" |

The global hotkey on Linux is not swallowed, so the focused app also sees the key. Pick a key the game doesn't use (F6 is fine for most).

## Build

```
dotnet build -c Release
```

Requires the .NET 10 SDK. Settings are stored in `%AppData%\FortniteAFKXPMonitor\settings.json` (Windows) or `~/.config/FortniteAFKXPMonitor/settings.json` (Linux).

CI builds both platforms on every push to `main`, bumps the patch version (`[minor]` / `[major]` in a commit message bumps those), and publishes a GitHub release.

Replace `Assets/icon.ico` and `Assets/Icon.png` to change the app icon.
