using Avalonia.Input;

namespace FortniteAFKXPMonitor.Services;

public enum MouseButtonKind { Left, Right, Middle }

/// <summary>What the focused window looks like, in platform-neutral terms.</summary>
public sealed record WindowInfo(string Process, string Class, string Title)
{
    public static readonly WindowInfo None = new("", "", "");
}

public sealed record TargetInfo(bool IsTarget, string Label, string ProcessName);

/// <summary>Sends synthetic mouse / keyboard input.</summary>
public interface IInputSimulator : IDisposable
{
    void MouseDown(MouseButtonKind button);
    void MouseUp(MouseButtonKind button);
    void MouseMoveRelative(int dx, int dy);
    void KeyDown(Key key);
    void KeyUp(Key key);

    /// <summary>Non-null when input can't be sent (e.g. missing /dev/uinput permission on Linux).</summary>
    string? Problem { get; }
}

/// <summary>Finds the currently focused window.</summary>
public interface IWindowDetector
{
    WindowInfo GetForeground();

    /// <summary>How often the engine should call <see cref="GetForeground"/>. Cheap on Windows, costlier on Linux.</summary>
    int PollIntervalMs { get; }

    /// <summary>Non-null when focus detection isn't possible on this system.</summary>
    string? Problem { get; }

    /// <summary>
    /// The external tool this desktop session needs for focus detection (hyprctl, swaymsg, kdotool, xdotool),
    /// or null when none is needed (Windows) or the desktop isn't supported.
    /// </summary>
    string? RequiredTool { get; }

    /// <summary>Re-runs detection, e.g. after the user installed <see cref="RequiredTool"/> while the app was open.</summary>
    void Refresh();
}

/// <summary>Reports movement from a real (non-synthetic) mouse.</summary>
public interface IMouseActivityMonitor : IDisposable
{
    event Action? UserMoved;
    void Start();
    void Stop();
    string? Problem { get; }
}

/// <summary>Global start/stop hotkey that works while the game has focus.</summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Raised on an arbitrary thread when the hotkey is pressed.</summary>
    event Action? Pressed;

    /// <summary>Binds the hotkey. Returns false if it couldn't be registered.</summary>
    bool Register(Key key);

    /// <summary>
    /// True while Alt is physically held, so the engine can stay out of the way of Alt+Tab. Only Linux reports
    /// this: on Windows the Alt+Tab switcher becomes the foreground window, which the focus check already catches.
    /// </summary>
    bool AltHeld { get; }

    /// <summary>Linux: state of the desktop-portal global shortcut (which also catches Deskflow-forwarded keys), for the UI. Null elsewhere.</summary>
    string? GlobalShortcutStatus { get; }

    /// <summary>True when the desktop owns a global shortcut for the app whose key can be changed through <see cref="ConfigureGlobalShortcut"/>.</summary>
    bool CanConfigureGlobalShortcut { get; }

    /// <summary>Opens the desktop's own editor for the app's global shortcut (the key it's bound to lives in the desktop, not the app).</summary>
    void ConfigureGlobalShortcut();

    string? Problem { get; }
}
