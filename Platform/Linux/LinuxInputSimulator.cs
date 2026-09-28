using System.Text;
using Avalonia.Input;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Linux.LinuxNative;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Emits input through a virtual /dev/uinput device, which works on both X11 and Wayland
/// (no compositor cooperation needed). Requires write access to /dev/uinput.
/// </summary>
public sealed class LinuxInputSimulator : IInputSimulator
{
    public const string VirtualDeviceName = "FortniteAFKXPMonitor virtual input";

    private readonly object _lock = new();
    private int _fd = -1;

    public string? Problem { get; private set; }

    public LinuxInputSimulator()
    {
        try
        {
            CreateDevice();
        }
        catch (Exception ex)
        {
            Problem = $"Couldn't set up the virtual input device: {ex.Message}";
        }
    }

    private void CreateDevice()
    {
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            Problem = "Can't open /dev/uinput (permission denied or module not loaded). "
                      + "Add your user to the 'input' group and set up the udev rule from the README, then log out and back in.";
            return;
        }

        ioctl(fd, UI_SET_EVBIT, EV_KEY);
        ioctl(fd, UI_SET_EVBIT, EV_REL);
        ioctl(fd, UI_SET_EVBIT, EV_SYN);
        ioctl(fd, UI_SET_RELBIT, REL_X);
        ioctl(fd, UI_SET_RELBIT, REL_Y);
        ioctl(fd, UI_SET_KEYBIT, BTN_LEFT);
        ioctl(fd, UI_SET_KEYBIT, BTN_RIGHT);
        ioctl(fd, UI_SET_KEYBIT, BTN_MIDDLE);
        foreach (int code in KeyMap.AllEvdevCodes)
            ioctl(fd, UI_SET_KEYBIT, code);

        // struct uinput_setup { struct input_id id; char name[80]; __u32 ff_effects_max; } = 92 bytes
        var setup = new byte[92];
        BitConverter.GetBytes((ushort)0x03).CopyTo(setup, 0);   // BUS_USB
        BitConverter.GetBytes((ushort)0x1209).CopyTo(setup, 2); // vendor
        BitConverter.GetBytes((ushort)0x0001).CopyTo(setup, 4); // product
        BitConverter.GetBytes((ushort)1).CopyTo(setup, 6);      // version
        Encoding.UTF8.GetBytes(VirtualDeviceName).CopyTo(setup, 8);

        if (ioctl(fd, UI_DEV_SETUP, setup) < 0 || ioctl(fd, UI_DEV_CREATE, 0) < 0)
        {
            close(fd);
            Problem = "Couldn't create the uinput virtual device.";
            return;
        }

        _fd = fd;

        // Compositors need a moment to notice the new device before it can deliver events.
        Thread.Sleep(300);
    }

    public void MouseDown(MouseButtonKind b) => Emit((EV_KEY, ButtonCode(b), 1));

    public void MouseUp(MouseButtonKind b) => Emit((EV_KEY, ButtonCode(b), 0));

    public void MouseMoveRelative(int dx, int dy)
    {
        if (dx != 0 && dy != 0) Emit((EV_REL, REL_X, dx), (EV_REL, REL_Y, dy));
        else if (dx != 0) Emit((EV_REL, REL_X, dx));
        else if (dy != 0) Emit((EV_REL, REL_Y, dy));
    }

    public void KeyDown(Key key)
    {
        if (KeyMap.TryGetEvdev(key, out int code)) Emit((EV_KEY, (ushort)code, 1));
    }

    public void KeyUp(Key key)
    {
        if (KeyMap.TryGetEvdev(key, out int code)) Emit((EV_KEY, (ushort)code, 0));
    }

    private static ushort ButtonCode(MouseButtonKind b) => b switch
    {
        MouseButtonKind.Right => BTN_RIGHT,
        MouseButtonKind.Middle => BTN_MIDDLE,
        _ => BTN_LEFT,
    };

    /// <summary>Writes the events followed by a SYN_REPORT so the kernel delivers them together.</summary>
    private void Emit(params (ushort Type, ushort Code, int Value)[] events)
    {
        lock (_lock)
        {
            if (_fd < 0) return;

            var buf = new byte[InputEventSize * (events.Length + 1)];
            int i = 0;
            foreach (var e in events)
                Write(buf, ref i, e.Type, e.Code, e.Value);
            Write(buf, ref i, EV_SYN, 0, 0);

            write(_fd, buf, buf.Length);
        }
    }

    private static void Write(byte[] buf, ref int offset, ushort type, ushort code, int value)
    {
        // The first 16 bytes (timeval) stay zero; the kernel stamps the event itself.
        BitConverter.GetBytes(type).CopyTo(buf, offset + 16);
        BitConverter.GetBytes(code).CopyTo(buf, offset + 18);
        BitConverter.GetBytes(value).CopyTo(buf, offset + 20);
        offset += InputEventSize;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_fd < 0) return;
            ioctl(_fd, UI_DEV_DESTROY, 0);
            close(_fd);
            _fd = -1;
        }
    }
}
