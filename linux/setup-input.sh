#!/bin/sh
#
# One-time Linux setup for the Fortnite AFK XP Tool. Must run as root.
#
#   sudo ./setup-input.sh "$USER"
#
# The app also runs this itself (through pkexec) from its "Fix permissions" button.
#
# It lets the given user:
#   - create a virtual input device through /dev/uinput (to send clicks and keys), and
#   - read /dev/input events (for the global hotkey and detecting your real mouse),
# by adding them to the 'input' group, installing a udev rule for /dev/uinput, and loading the
# uinput module if the kernel doesn't already have it built in.
#
# Log out and back in afterwards so the new group membership applies.
#
# Exit codes: 0 ok, 1 not root, 2 bad arguments, 3 the kernel has no uinput.

set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "This must run as root: sudo $0 \"\$USER\"" >&2
  exit 1
fi

user="${1:-${SUDO_USER:-}}"
if [ -z "$user" ]; then
  echo "Usage: sudo $0 USERNAME" >&2
  exit 2
fi
case "$user" in
  -*|*[!A-Za-z0-9._-]*)
    echo "Refusing odd-looking user name: $user" >&2
    exit 2 ;;
esac
if ! id "$user" >/dev/null 2>&1; then
  echo "No such user: $user" >&2
  exit 2
fi

# Overridable so the script can be tested without touching the real system.
UDEV_DIR="${UDEV_DIR:-/etc/udev/rules.d}"
MODULES_LOAD_DIR="${MODULES_LOAD_DIR:-/etc/modules-load.d}"
UINPUT_NODE="${UINPUT_NODE:-/dev/uinput}"

# 1. The 'input' group, and the user in it.
if ! getent group input >/dev/null 2>&1; then
  groupadd -r input
fi
if id -nG "$user" | tr ' ' '\n' | grep -qx input; then
  echo "$user is already in the input group."
else
  usermod -aG input "$user"
  echo "Added $user to the input group."
fi

# 2. udev rule: /dev/uinput usable by the input group.
mkdir -p "$UDEV_DIR"
cat > "$UDEV_DIR/99-fortnite-afk-xp.rules" <<'EOF'
# Lets members of the "input" group create virtual input devices (needed to send clicks/keys on Wayland and X11).
KERNEL=="uinput", MODE="0660", GROUP="input", OPTIONS+="static_node=uinput"
EOF
echo "Installed $UDEV_DIR/99-fortnite-afk-xp.rules"
udevadm control --reload-rules

# 3. Load uinput only if it isn't already there. Many kernels (SteamOS / CachyOS "deckify" handheld
#    builds, ...) have it built in, in which case there's no module to load.
if [ ! -e "$UINPUT_NODE" ]; then
  if modprobe uinput 2>/dev/null; then
    mkdir -p "$MODULES_LOAD_DIR"
    echo uinput > "$MODULES_LOAD_DIR/uinput.conf"
    echo "Loaded the uinput module and set it to load at boot."
  fi
fi

udevadm trigger

if [ ! -e "$UINPUT_NODE" ]; then
  echo "/dev/uinput still doesn't exist: this kernel was built without uinput. You'll need one that has it (e.g. linux-cachyos)." >&2
  exit 3
fi

echo "Done. Log out and back in so the group change applies."
