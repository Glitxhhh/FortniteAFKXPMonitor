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

    public event Action? Pressed;

    public string? Problem { get; private set; }

    public bool Register(Key key)
    {
        if (!KeyMap.TryGetEvdev(key, out int code))
        {
            Problem = $"{key} can't be used as a hotkey.";
            return false;
        }

        _code = code;

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

            _reader = new EvdevReader(paths, OnEvent);
        }

        Problem = null;
        return true;
    }

    private void OnEvent(ushort type, ushort code, int value)
    {
        // value 1 = press; 0 = release, 2 = auto-repeat (ignored)
        if (type == EV_KEY && value == 1 && code == _code)
            Pressed?.Invoke();
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
    }
}
