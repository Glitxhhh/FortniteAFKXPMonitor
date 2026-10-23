// Linux-only, but the assembly is shared with Windows.
#pragma warning disable CA1416

using System.Runtime.InteropServices;
using Avalonia.Input;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Global hotkey and Alt tracking for X11 sessions (GNOME on X11, XFCE, Cinnamon, MATE, i3, KDE on X11, ...).
///
/// Deskflow / Synergy / Input Leap inject keys into an X11 session through XTest, so they never appear in
/// /dev/input. The X server does see them, so a passive key grab (XGrabKey) catches the hotkey, and
/// XQueryKeymap reports whether Alt is down, for any keyboard.
/// </summary>
internal sealed class X11Keys : IDisposable
{
    private const int KeyPress = 2;
    private const uint LockMask = 1 << 1;     // Caps Lock
    private const uint Mod2Mask = 1 << 4;     // Num Lock
    private const int BadAccess = 10;
    private const ulong XK_Alt_L = 0xFFE9, XK_Alt_R = 0xFFEA;

    // XGrabKey must be repeated for each Caps/Num Lock state, or the key stops working while a lock is on.
    private static readonly uint[] LockCombos = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };

    private delegate int XErrorHandler(IntPtr display, ref XErrorEvent error);
    private static readonly XErrorHandler ErrorHandler = OnXError;   // kept in a field so the GC can't collect it
    private static volatile bool _grabDenied;

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int type;
        public IntPtr display;
        public ulong resourceid;
        public ulong serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, int discard);
    [DllImport("libX11.so.6")] private static extern int XQueryKeymap(IntPtr display, byte[] keys);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _grabDisplay;      // owned by the event thread once started
    private IntPtr _queryDisplay;     // used by AltHeld
    private int _hotkeyKeycode;
    private int _altL, _altR;
    private long _altCheckedAt;
    private bool _altCached;

    public event Action? Pressed;

    public string Status { get; private set; } = "";

    /// <summary>True when an X server is reachable (DISPLAY is set and libX11 loads).</summary>
    public bool Available { get; private set; }

    /// <summary>Opens the display and starts watching for the hotkey. Returns false if X isn't usable.</summary>
    public bool Start(Key hotkey)
    {
        try
        {
            XSetErrorHandler(ErrorHandler);   // the default handler exits the process on a failed grab
            _queryDisplay = XOpenDisplay(null);
            _grabDisplay = XOpenDisplay(null);
            if (_queryDisplay == IntPtr.Zero || _grabDisplay == IntPtr.Zero)
                return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        _altL = XKeysymToKeycode(_queryDisplay, XK_Alt_L);
        _altR = XKeysymToKeycode(_queryDisplay, XK_Alt_R);
        Available = true;
        SetHotkey(hotkey);
        new Thread(EventLoop) { IsBackground = true, Name = "X11 hotkey" }.Start();
        return true;
    }

    /// <summary>Moves the grab to a different key (the user picked a new hotkey).</summary>
    public void SetHotkey(Key hotkey)
    {
        if (!Available) return;

        lock (_lock)
        {
            IntPtr root = XDefaultRootWindow(_grabDisplay);
            if (_hotkeyKeycode != 0)
                foreach (uint combo in LockCombos)
                    XUngrabKey(_grabDisplay, _hotkeyKeycode, combo, root);
            _hotkeyKeycode = 0;

            ulong? keysym = KeySym(hotkey);
            int keycode = keysym is { } k ? XKeysymToKeycode(_grabDisplay, k) : 0;
            if (keycode == 0)
            {
                Status = $"{hotkey} can't be used as an X11 hotkey.";
                return;
            }

            _grabDenied = false;
            foreach (uint combo in LockCombos)
                XGrabKey(_grabDisplay, keycode, combo, root, 0, 1 /* GrabModeAsync */, 1);
            XSync(_grabDisplay, 0);   // surfaces a BadAccess right here instead of later

            if (_grabDenied)
            {
                Status = $"{hotkey} is already grabbed by another program, so the hotkey only works from keyboards the app can read directly.";
                return;
            }

            _hotkeyKeycode = keycode;
            Status = $"Also grabbed through X11: {hotkey} (works for keyboards forwarded by Deskflow and similar)";
        }
    }

    /// <summary>True while Alt is down on any keyboard the X server knows about. Cheap to call often.</summary>
    public bool AltHeld
    {
        get
        {
            if (!Available) return false;

            lock (_lock)
            {
                long now = Environment.TickCount64;
                if (now - _altCheckedAt < 10)      // the engine polls every ~2 ms; ask the server at most every 10
                    return _altCached;
                _altCheckedAt = now;

                var keys = new byte[32];
                XQueryKeymap(_queryDisplay, keys);
                _altCached = IsDown(keys, _altL) || IsDown(keys, _altR);
                return _altCached;
            }
        }
    }

    private static bool IsDown(byte[] keymap, int keycode) =>
        keycode > 0 && (keymap[keycode / 8] & (1 << (keycode % 8))) != 0;

    private void EventLoop()
    {
        IntPtr evt = Marshal.AllocHGlobal(192);   // sizeof(XEvent)
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool any = false;
                lock (_lock)
                {
                    while (XPending(_grabDisplay) > 0)
                    {
                        any = true;
                        XNextEvent(_grabDisplay, evt);
                        if (Marshal.ReadInt32(evt) == KeyPress)
                            Pressed?.Invoke();
                    }
                }
                if (!any)
                    Thread.Sleep(10);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(evt);
        }
    }

    private static int OnXError(IntPtr display, ref XErrorEvent error)
    {
        if (error.error_code == BadAccess)
            _grabDenied = true;   // someone else already holds this key
        return 0;
    }

    /// <summary>X11 keysym for the key, or null for keys we don't map.</summary>
    private static ulong? KeySym(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return 0xFFBE + (ulong)(key - Key.F1);
        if (key is >= Key.A and <= Key.Z) return 0x61 + (ulong)(key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return 0x30 + (ulong)(key - Key.D0);
        return key switch
        {
            Key.Space => 0x20,
            Key.Return => 0xFF0D,
            Key.Escape => 0xFF1B,
            Key.Tab => 0xFF09,
            Key.Home => 0xFF50,
            Key.Left => 0xFF51,
            Key.Up => 0xFF52,
            Key.Right => 0xFF53,
            Key.Down => 0xFF54,
            Key.PageUp => 0xFF55,
            Key.PageDown => 0xFF56,
            Key.End => 0xFF57,
            Key.Insert => 0xFF63,
            Key.Delete => 0xFFFF,
            _ => null,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        Thread.Sleep(30);   // let the event loop leave its last XPending before the connections close
        lock (_lock)
        {
            if (_grabDisplay != IntPtr.Zero) { XCloseDisplay(_grabDisplay); _grabDisplay = IntPtr.Zero; }
            if (_queryDisplay != IntPtr.Zero) { XCloseDisplay(_queryDisplay); _queryDisplay = IntPtr.Zero; }
            Available = false;
        }
        _cts.Dispose();
    }
}
