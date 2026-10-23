using Avalonia.Input;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Linux.LinuxNative;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Global hotkey by watching keyboards through evdev. This works on Wayland, where apps can't grab
/// global keys, but the key press is not swallowed: the focused app sees it too.
/// </summary>
public sealed class LinuxHotkeyService : IHotkeyService
{
    private EvdevReader? _reader;
    private volatile int _code = -1;
    private PortalShortcuts? _portal;
    private readonly KdeSwitcherWatcher _switcher = new();
    private int _altMask;   // bit 0 = left Alt held, bit 1 = right Alt held

    public event Action? Pressed;

    public string? Problem { get; private set; }

    public string? GlobalShortcutStatus => _portal?.Status;

    public bool CanConfigureGlobalShortcut => _portal?.IsBound == true;

    public void ConfigureGlobalShortcut() => _portal?.Configure();

    /// <summary>
    /// True while a window switch (Alt+Tab) is in progress: Alt is held on a keyboard the app can read, or - on KDE,
    /// for any keyboard including Deskflow-forwarded ones - KWin's window switcher is open.
    /// </summary>
    public bool AltHeld => Volatile.Read(ref _altMask) != 0 || _switcher.Active;

    public bool Register(Key key)
    {
        if (!KeyMap.TryGetEvdev(key, out int code))
        {
            Problem = $"{key} can't be used as a hotkey.";
            return false;
        }

        _code = code;

        // Also ask the desktop for the key: that path sees keys forwarded by Deskflow & co, which never reach /dev/input.
        // Started once; the desktop keeps its own binding afterwards (changed in System Settings, not here).
        if (_portal is null)
        {
            _portal = new PortalShortcuts();
            _portal.Activated += () => Pressed?.Invoke();
            _portal.Start(key);
            _switcher.Start();   // KDE only: tells us when the Alt+Tab switcher is open, for any keyboard
        }
        else
        {
            _portal.Rebind(key);   // the user picked a new key in the app: move the desktop's binding too (KDE)
        }

        if (_reader is null)
        {
            var paths = EvdevScanner.Scan().Where(d => d.IsKeyboard).Select(d => d.Path).ToList();
            if (paths.Count == 0)
            {
                Problem = EvdevScanner.AnyDeviceNodes
                    ? "Can't read /dev/input devices, so the global hotkey won't work. Add your user to the 'input' group."
                    : "No keyboard found for the global hotkey.";
                return false;
            }

            _reader = new EvdevReader(paths, OnEvent, onLost: () => Volatile.Write(ref _altMask, 0));
        }

        Problem = null;
        return true;
    }

    private void OnEvent(ushort type, ushort code, int value)
    {
        if (type != EV_KEY)
            return;

        // Track Alt so the engine can pause while you alt-tab. (A keyboard that disappears while Alt is down
        // clears the state in the reader's onLost, so it can't get stuck.)
        if (code == KEY_LEFTALT || code == KEY_RIGHTALT)
        {
            int bit = code == KEY_LEFTALT ? 1 : 2;
            if (value == 1) Interlocked.Or(ref _altMask, bit);
            else if (value == 0) Interlocked.And(ref _altMask, ~bit);
        }

        // value 1 = press; 0 = release, 2 = auto-repeat (ignored)
        if (value == 1 && code == _code)
            Pressed?.Invoke();
    }

    public void Dispose()
    {
        _switcher.Dispose();
        _portal?.Dispose();
        _reader?.Dispose();
        _reader = null;
    }
}
