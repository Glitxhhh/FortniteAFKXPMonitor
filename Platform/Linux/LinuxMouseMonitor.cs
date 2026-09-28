using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Linux.LinuxNative;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Watches physical mice through evdev. Our own virtual uinput device is excluded, so the
/// tool's camera nudges never count as the user moving the mouse.
/// </summary>
public sealed class LinuxMouseMonitor : IMouseActivityMonitor
{
    // Raw counts, not pixels; a real camera move adds up to this almost instantly.
    private const int MinDistance = 4;

    private EvdevReader? _reader;
    private int _accum;

    public event Action? UserMoved;

    public string? Problem { get; private set; }

    public void Start()
    {
        if (_reader is not null) return;
        _accum = 0;

        var paths = EvdevScanner.Scan().Where(d => d.IsMouse).Select(d => d.Path).ToList();
        if (paths.Count == 0)
        {
            Problem = EvdevScanner.AnyDeviceNodes
                ? "Can't read /dev/input devices, so real mouse movement can't be detected. Add your user to the 'input' group."
                : "No input devices found.";
            return;
        }

        Problem = null;
        _reader = new EvdevReader(paths, OnEvent);
    }

    private void OnEvent(ushort type, ushort code, int value)
    {
        if (type != EV_REL || (code != REL_X && code != REL_Y))
            return;

        _accum += Math.Abs(value);
        if (_accum >= MinDistance)
        {
            _accum = 0;
            UserMoved?.Invoke();
        }
    }

    public void Stop()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}
