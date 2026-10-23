#!/usr/bin/env bash
#
# Installs the window-focus tool that the Fortnite AFK XP Tool needs on Linux.
#
# The app only sends input while your game is focused. Wayland has no generic "which window is
# focused" API, so the app shells out to a tool for your session:
#
#   Hyprland              hyprctl   (ships with Hyprland)
#   Sway                  swaymsg   (ships with Sway)
#   niri                  niri      (ships with niri)
#   KDE Plasma (Wayland)  kdotool
#   X11 (any desktop)     xdotool
#
# This script works out which one your session needs, works out your distro, and installs it.
# Run it from inside your desktop session (not over ssh or from a TTY), as your normal user.
#
# Run with --help for options.

set -euo pipefail

DRY_RUN=0
ASSUME_YES=0
FORCE_TOOL=""
OS_RELEASE="${OS_RELEASE:-/etc/os-release}"   # overridable so the detection can be tested

if [ -t 1 ]; then
  C_OK=$'\e[32m'; C_WARN=$'\e[33m'; C_ERR=$'\e[31m'; C_OFF=$'\e[0m'
else
  C_OK=""; C_WARN=""; C_ERR=""; C_OFF=""
fi

say()  { printf '%s\n' "$*"; }
ok()   { printf '%s[ok]%s %s\n' "$C_OK" "$C_OFF" "$*"; }
warn() { printf '%s[!]%s %s\n' "$C_WARN" "$C_OFF" "$*" >&2; }
die()  { printf '%s[x]%s %s\n' "$C_ERR" "$C_OFF" "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

usage() {
  cat <<'EOF'
Installs the window-focus tool the Fortnite AFK XP Tool needs on Linux.

Usage: install-focus-tool.sh [options]

  --tool NAME    install NAME (hyprctl, swaymsg, niri, kdotool or xdotool) instead of detecting it
  -y, --yes      don't ask for confirmation (passes --noconfirm / -y to the package manager)
  -n, --dry-run  show what would be done without installing anything
  -h, --help     show this help

Environment:
  KDOTOOL_AUR_PKG=kdotool-bin   on Arch-based distros, install the prebuilt AUR package
                                instead of building kdotool from source
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --tool)       [ $# -ge 2 ] || die "--tool needs a value"; FORCE_TOOL="$2"; shift 2 ;;
    --tool=*)     FORCE_TOOL="${1#--tool=}"; shift ;;
    -y|--yes)     ASSUME_YES=1; shift ;;
    -n|--dry-run) DRY_RUN=1; shift ;;
    -h|--help)    usage; exit 0 ;;
    *)            die "Unknown option: $1 (try --help)" ;;
  esac
done

run() {
  if [ "$DRY_RUN" -eq 1 ]; then
    printf '  [dry-run] %s\n' "$*"
    return 0
  fi
  printf '  $ %s\n' "$*"
  "$@"
}

confirm() {
  if [ "$ASSUME_YES" -eq 1 ] || [ "$DRY_RUN" -eq 1 ]; then
    return 0
  fi
  local reply
  if ! read -r -p "$1 [Y/n] " reply; then
    die "No terminal to ask on - rerun with --yes to skip the confirmation."
  fi
  case "${reply:-y}" in
    [Yy]*) return 0 ;;
    *)     die "Cancelled." ;;
  esac
}

# ---- sudo -------------------------------------------------------------------

SUDO="sudo"
if [ "$(id -u)" -eq 0 ]; then
  if [ "$DRY_RUN" -eq 0 ]; then
    die "Run this as your normal user, not root. It calls sudo itself, and AUR helpers refuse to run as root."
  fi
  SUDO=""
elif [ "$DRY_RUN" -eq 0 ] && ! have sudo; then
  die "sudo isn't installed, and it's needed to install packages."
fi

# ---- distro -----------------------------------------------------------------

DISTRO_ID=""
DISTRO_NAME="unknown"
FAMILY=""

os_release_value() {
  [ -r "$OS_RELEASE" ] || return 0
  sed -n "s/^$1=//p" "$OS_RELEASE" | head -n1 | tr -d "\"'"
}

detect_distro() {
  DISTRO_ID=$(os_release_value ID)
  local like name
  like=$(os_release_value ID_LIKE)
  name=$(os_release_value PRETTY_NAME)
  DISTRO_NAME="${name:-${DISTRO_ID:-unknown}}"

  # Match the distro itself or anything it says it's derived from (CachyOS reports ID_LIKE=arch).
  case " $DISTRO_ID $like " in
    *" arch "*|*" cachyos "*|*" endeavouros "*|*" manjaro "*|*" garuda "*|*" artix "*) FAMILY=arch ;;
    *" debian "*|*" ubuntu "*)                                                         FAMILY=debian ;;
    *" fedora "*|*" rhel "*|*" centos "*)                                              FAMILY=fedora ;;
    *" suse "*|*" opensuse "*|*" opensuse-leap "*|*" opensuse-tumbleweed "*)           FAMILY=suse ;;
    *)                                                                                 FAMILY="" ;;
  esac

  # Unknown name: fall back to whichever package manager is present.
  if [ -z "$FAMILY" ]; then
    if   have pacman;  then FAMILY=arch
    elif have apt-get; then FAMILY=debian
    elif have dnf;     then FAMILY=fedora
    elif have zypper;  then FAMILY=suse
    fi
  fi
}

# ---- which tool does this session need? (same order as the app's own detection) ----

TOOL=""
REASON=""

detect_tool() {
  local session="${XDG_SESSION_TYPE:-}" desktop="${XDG_CURRENT_DESKTOP:-}" wayland=0
  session="${session,,}"
  desktop="${desktop^^}"
  if [ "$session" = "wayland" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; then
    wayland=1
  fi

  if [ -n "${HYPRLAND_INSTANCE_SIGNATURE:-}" ]; then
    TOOL=hyprctl; REASON="Hyprland session"
  elif [ -n "${SWAYSOCK:-}" ]; then
    TOOL=swaymsg; REASON="Sway session"
  elif [ -n "${NIRI_SOCKET:-}" ]; then
    TOOL=niri; REASON="niri session"
  elif [ "$wayland" -eq 1 ] && [[ "$desktop" == *KDE* ]]; then
    TOOL=kdotool; REASON="KDE Plasma on Wayland"
  elif [ "$wayland" -eq 0 ] && [ -n "${DISPLAY:-}" ]; then
    TOOL=xdotool; REASON="X11 session"
  fi
}

# ---- package manager helpers -------------------------------------------------

YES_FLAG=()
if [ "$ASSUME_YES" -eq 1 ]; then
  YES_FLAG=("-y")
fi

pm_name() {
  case "$FAMILY" in
    arch) echo pacman ;; debian) echo apt ;; fedora) echo dnf ;; suse) echo zypper ;; *) echo "" ;;
  esac
}

pm_has_pkg() {
  case "$FAMILY" in
    arch)   pacman -Si "$1" >/dev/null 2>&1 ;;
    debian) apt-cache show "$1" >/dev/null 2>&1 ;;
    fedora) dnf -q info "$1" >/dev/null 2>&1 ;;
    suse)   zypper -q info "$1" >/dev/null 2>&1 ;;
    *)      return 1 ;;
  esac
}

pm_install() {
  case "$FAMILY" in
    arch)
      local flags=(--needed)
      [ "$ASSUME_YES" -eq 1 ] && flags+=(--noconfirm)
      run $SUDO pacman -S "${flags[@]}" "$@" ;;
    debian) run $SUDO apt-get install ${YES_FLAG[@]+"${YES_FLAG[@]}"} "$@" ;;
    fedora) run $SUDO dnf install ${YES_FLAG[@]+"${YES_FLAG[@]}"} "$@" ;;
    suse)   run $SUDO zypper install ${YES_FLAG[@]+"${YES_FLAG[@]}"} "$@" ;;
    *)      die "Couldn't work out your package manager. Install this yourself: $*" ;;
  esac
}

check_not_immutable() {
  if [ "$DISTRO_ID" = "steamos" ] || [ -e /run/ostree-booted ]; then
    die "This is an immutable system ($DISTRO_NAME), where packages can't be installed the usual way. Install $TOOL manually (e.g. in a distrobox container), or turn off \"Only send input while the game is focused\" in the app."
  fi
}

# ---- installers ---------------------------------------------------------------

install_xdotool() {
  confirm "Install xdotool with $(pm_name)?"
  [ "$FAMILY" = "debian" ] && run $SUDO apt-get update
  pm_install xdotool
}

install_kdotool_aur() {
  local pkg="${KDOTOOL_AUR_PKG:-kdotool}"
  local helper="" h
  for h in paru yay; do
    if have "$h"; then helper="$h"; break; fi
  done

  if [ -n "$helper" ]; then
    say "Using the AUR helper '$helper' (you'll get to review the PKGBUILD)."
    local flags=(--needed)
    [ "$ASSUME_YES" -eq 1 ] && flags+=(--noconfirm)
    run "$helper" -S "${flags[@]}" "$pkg"
    return
  fi

  warn "No AUR helper (paru / yay) found - building the AUR package by hand with makepkg."
  pm_install base-devel git
  local tmp
  tmp=$(mktemp -d)
  run git clone --depth 1 "https://aur.archlinux.org/${pkg}.git" "$tmp/$pkg"
  say "  PKGBUILD to review: $tmp/$pkg/PKGBUILD"
  if [ "$DRY_RUN" -eq 1 ]; then
    printf '  [dry-run] (cd %s && makepkg -si)\n' "$tmp/$pkg"
  else
    local mflags=(-si)
    [ "$ASSUME_YES" -eq 1 ] && mflags+=(--noconfirm)
    (cd "$tmp/$pkg" && makepkg "${mflags[@]}")
  fi
  rm -rf "$tmp"
}

install_kdotool_cargo() {
  say "kdotool isn't packaged for $DISTRO_NAME; building it from crates.io with cargo."
  # Build deps: a compiler, pkg-config and the libdbus headers (kdotool talks to KWin over D-Bus).
  case "$FAMILY" in
    debian) pm_install build-essential pkg-config libdbus-1-dev ;;
    fedora) pm_install gcc pkgconf-pkg-config dbus-devel ;;
    suse)   pm_install gcc pkg-config dbus-1-devel ;;
    *)      die "Don't know the build dependencies for this distro. Install a C compiler, pkg-config, the libdbus development headers and Rust, then run: cargo install --locked --root ~/.local kdotool" ;;
  esac
  # Don't shadow an existing (e.g. rustup) toolchain with the distro's older cargo.
  have cargo || pm_install cargo
  run cargo install --locked --root "$HOME/.local" kdotool
  warn "If the build fails complaining about the Rust version, install a current toolchain from https://rustup.rs and rerun."
}

install_kdotool() {
  confirm "Install kdotool for $DISTRO_NAME?"
  [ "$FAMILY" = "debian" ] && run $SUDO apt-get update

  if pm_has_pkg kdotool; then
    pm_install kdotool
  elif [ "$FAMILY" = "arch" ]; then
    say "kdotool isn't in your enabled pacman repos; it's in the AUR."
    install_kdotool_aur
  else
    install_kdotool_cargo
  fi
}

# ---- main ---------------------------------------------------------------------

detect_distro
say "Distro:  $DISTRO_NAME (package family: ${FAMILY:-unknown})"

case "$FORCE_TOOL" in
  "")                                    detect_tool ;;
  hyprctl|swaymsg|niri|kdotool|xdotool) TOOL="$FORCE_TOOL"; REASON="requested with --tool" ;;
  *) die "--tool must be one of: hyprctl, swaymsg, niri, kdotool, xdotool" ;;
esac

if [ -z "$TOOL" ]; then
  warn "Couldn't find a supported focus tool for this session (XDG_SESSION_TYPE='${XDG_SESSION_TYPE:-}', XDG_CURRENT_DESKTOP='${XDG_CURRENT_DESKTOP:-}')."
  say "  - GNOME on Wayland has no way to query the focused window. Turn off \"Only send input while the game is focused\" in the app."
  say "  - Running over ssh or from a TTY? Rerun this inside your desktop session, or pass --tool NAME."
  exit 1
fi

say "Session: $REASON -> needs $TOOL"

if have "$TOOL"; then
  ok "$TOOL is already installed ($(command -v "$TOOL")). Nothing to do."
  exit 0
fi

case "$TOOL" in
  hyprctl) die "hyprctl ships with Hyprland itself but isn't on your PATH. Reinstall the 'hyprland' package, or check your PATH." ;;
  niri)    die "niri ships with the niri compositor itself but isn't on your PATH. Reinstall the 'niri' package, or check your PATH." ;;
  swaymsg) die "swaymsg ships with Sway itself but isn't on your PATH. Reinstall the 'sway' package, or check your PATH." ;;
esac

check_not_immutable

case "$TOOL" in
  xdotool) install_xdotool ;;
  kdotool) install_kdotool ;;
esac

if [ "$DRY_RUN" -eq 1 ]; then
  say "Dry run only - nothing was installed."
  exit 0
fi

hash -r
if ! have "$TOOL"; then
  warn "$TOOL installed, but it isn't on your PATH yet. For cargo builds that's ~/.local/bin - add it to PATH (and log out and in), then rerun this script to check."
  exit 1
fi
ok "$TOOL installed: $(command -v "$TOOL")"

# Make sure it can actually see the focused window from here.
if id=$("$TOOL" getactivewindow 2>/dev/null); then
  ok "$TOOL can see the focused window (id $id)."
else
  warn "$TOOL is installed but couldn't query the focused window from this shell. Run this from a terminal inside your desktop session."
fi

say
say "Done. Start the tool - the status line should read 'Active in: ...' while your game is focused."
say "Input permissions (uinput) are a separate one-time setup: see the Linux section of the README."
