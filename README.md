# Fortnite AFK XP Tool

A small WPF (.NET 10) utility for AFK XP sessions on **GeForce NOW**, **GeForce Infinity**, **Amazon Luna**, **Xbox Cloud Gaming** and the native **Fortnite** client.

## Features

- **Input modes**: mouse click (left/right/middle) or any keystroke, each in
  - **Auto** - repeats at a configurable interval, or
  - **Hold** - holds the button/key down continuously.
- **Camera nudge**: every ~5 minutes (±10%, capped at 6:30) the camera moves a little and returns. Each nudge varies in axis, direction, distance, speed, curve and dwell time (net movement is always zero), so the 7-minute AFK XP timeout never trips.
- **Focus-aware**: input is only sent while Fortnite / GeForce NOW / GeForce Infinity / Amazon Luna / Xbox Cloud (or a browser tab titled GeForce NOW / Xbox / Fortnite / Luna) is focused. Alt-tab away and input pauses automatically (held keys are released); return and it resumes, no toggling needed.
- **Countdown**: driven by the start/stop toggle, not the active window. Toggling off freezes it, toggling on resumes it. While the clicker is on, the countdown keeps running even if you're tabbed out.
- **Global hotkey** (default `F6`) to start/stop from inside the game. Click the hotkey box to rebind.

## Build

```
dotnet build -c Release
```

Requires the .NET 10 SDK on Windows. Output: `bin/Release/net10.0-windows/FortniteAFKXPMonitor.exe`.

Settings are stored in `%AppData%\FortniteAFKXPMonitor\settings.json`.

## Notes

- Input is sent with `SendInput` using scan codes. If the game/streaming client runs as administrator, run this tool as administrator too.
- Replace `Assets/icon.ico` to change the app icon.
