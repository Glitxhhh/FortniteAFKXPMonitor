# Fortnite AFK XP Tool

A small cross-platform (.NET 10 + Avalonia) utility for AFK XP sessions on **GeForce NOW**, **GeForce Infinity**, **Amazon Luna**, **Xbox Cloud Gaming** and the native **Fortnite** client. Runs on Windows and Linux.

> [!CAUTION]
> This tool simulates input and automates camera movement, which may be against the terms of service of Fortnite, GeForce NOW, GeForce Infinity, Amazon Luna or Xbox Cloud Gaming. There is no guarantee it won't be detected now or in the future. **Use at your own risk** - the author is not responsible for banned, suspended or restricted accounts resulting from the use of this tool.

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

Download the `linux-x64` archive from Releases and run `FortniteAFKXPMonitor`. Input is sent through a virtual `/dev/uinput` device and real mouse/keyboard events are read from `/dev/input`, so it works on Wayland and X11 (same approach as [CachyAutoClicker](https://github.com/Glitxhhh/CachyAutoClicker)).

### First-run setup

On launch the app checks what's in place and, if something's missing, shows a **Setup needed** card with a button for each fix:

- **Fix permissions** - asks for your password through the system's authentication dialog (polkit), then adds you to the `input` group, installs a udev rule for `/dev/uinput`, and loads the `uinput` module if the kernel doesn't already have it built in (many do, including the CachyOS handheld builds). **Log out and back in afterwards** so the group change applies, then start the app again.
- **Install focus tool** - opens a terminal running `install-focus-tool.sh` (below), since it needs your sudo password and, on Arch, a chance to review the AUR package. The card updates by itself when it's done.

There's no "setup done" flag to go stale: the checks look at the real system state every launch, and the card disappears once everything works. If `/dev/uinput` still doesn't exist after setup, your kernel was built without `uinput` and you'll need one that has it (e.g. `linux-cachyos`).

Prefer a terminal? Both steps are plain scripts in the archive:

```bash
sudo ./setup-input.sh "$USER"      # permissions: input group + udev rule (+ uinput module if needed); then log out and back in
./install-focus-tool.sh            # focus tool; add --dry-run to see what it would do first, --yes to skip prompts
```

### Focus detection

Wayland has no generic way to ask which window is focused, so the tool needed depends on your desktop:

| Desktop | Needs |
| --- | --- |
| Hyprland | `hyprctl` (included) |
| Sway | `swaymsg` (included) |
| niri | `niri` (included) |
| KDE Plasma (Wayland) | [`kdotool`](https://github.com/jinliu/kdotool) (AUR on Arch-based distros) |
| Any X11 session | `xdotool` |
| GNOME (Wayland) | not supported - turn off "Only send input while the game is focused" |

`install-focus-tool.sh` works out your session and distro and installs the right tool. It supports Arch-based distros (pacman, plus `paru`/`yay` or a manual `makepkg` for the AUR), Debian/Ubuntu, Fedora and openSUSE. `kdotool` is only packaged in the AUR, so on the other families it's built from crates.io with `cargo` (the script installs the build dependencies; if the build complains about the Rust version, install a current toolchain from [rustup.rs](https://rustup.rs)). `hyprctl`, `swaymsg` and `niri` come with their compositors, so the script only checks they're present. Immutable systems like SteamOS aren't handled.

The global hotkey on Linux is not swallowed, so the focused app also sees the key. Pick a key the game doesn't use (F6 is fine for most).

## Build

```
dotnet build -c Release
```

Requires the .NET 10 SDK. Settings are stored in `%AppData%\FortniteAFKXPMonitor\settings.json` (Windows) or `~/.config/FortniteAFKXPMonitor/settings.json` (Linux).

CI builds both platforms on every push to `main`, bumps the patch version (`[minor]` / `[major]` in a commit message bumps those), and publishes a GitHub release.

To change the app icon, replace `Assets/Icon.png` (any resolution - it's what the window itself displays)
and push. CI regenerates `Assets/icon.ico` from it automatically and commits the result back if it
changed, so you don't need to run anything locally. Since that push then only touches the two icon files,
it skips the version bump and replaces the builds on the current release instead of cutting a new one.

To regenerate it yourself (e.g. to preview before pushing):

```
python scripts/generate-icon.py
```

Requires Pillow (`pip install pillow`). `.ico` files can't hold anything bigger than 256x256 - that's a
limit of the format itself, not this script - so it's generated at 16 up to 256.
