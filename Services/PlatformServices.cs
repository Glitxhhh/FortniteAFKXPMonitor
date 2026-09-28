using FortniteAFKXPMonitor.Platform.Linux;
using FortniteAFKXPMonitor.Platform.Windows;

namespace FortniteAFKXPMonitor.Services;

/// <summary>The OS-specific pieces, picked once at startup.</summary>
public sealed class PlatformServices : IDisposable
{
    public IInputSimulator Input { get; }
    public IWindowDetector WindowDetector { get; }
    public IMouseActivityMonitor Mouse { get; }
    public IHotkeyService Hotkey { get; }

    private PlatformServices(IInputSimulator input, IWindowDetector windows, IMouseActivityMonitor mouse, IHotkeyService hotkey)
    {
        Input = input;
        WindowDetector = windows;
        Mouse = mouse;
        Hotkey = hotkey;
    }

    public static PlatformServices Create()
    {
        if (OperatingSystem.IsWindows())
            return new PlatformServices(new WindowsInputSimulator(), new WindowsWindowDetector(),
                new WindowsMouseMonitor(), new WindowsHotkeyService());

        if (OperatingSystem.IsLinux())
            return new PlatformServices(new LinuxInputSimulator(), new LinuxWindowDetector(),
                new LinuxMouseMonitor(), new LinuxHotkeyService());

        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }

    public void Dispose()
    {
        Hotkey.Dispose();
        Mouse.Dispose();
        Input.Dispose();
    }
}
