using System.Text;
using static FortniteAFKXPMonitor.Platform.Linux.LinuxNative;

namespace FortniteAFKXPMonitor.Platform.Linux;

public sealed record EvdevDevice(string Path, string Name, bool IsMouse, bool IsKeyboard);

/// <summary>Finds /dev/input/event* devices and classifies them.</summary>
public static class EvdevScanner
{
    public static bool AnyDeviceNodes => Directory.Exists("/dev/input")
                                         && Directory.EnumerateFiles("/dev/input", "event*").Any();

    /// <summary>Real input devices we're allowed to read. Our own virtual device is excluded.</summary>
    public static List<EvdevDevice> Scan()
    {
        var result = new List<EvdevDevice>();
        if (!Directory.Exists("/dev/input"))
            return result;

        foreach (string path in Directory.EnumerateFiles("/dev/input", "event*"))
        {
            int fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0)
                continue; // no permission or vanished

            try
            {
                var nameBuf = new byte[256];
                string name = ioctl(fd, EVIOCGNAME(nameBuf.Length), nameBuf) >= 0
                    ? Encoding.UTF8.GetString(nameBuf).TrimEnd('\0')
                    : "";

                if (name == LinuxInputSimulator.VirtualDeviceName)
                    continue;

                var types = new byte[4];
                ioctl(fd, EVIOCGBIT(0, types.Length), types);

                bool isMouse = false;
                if (TestBit(types, EV_REL))
                {
                    var rel = new byte[2];
                    ioctl(fd, EVIOCGBIT(EV_REL, rel.Length), rel);
                    isMouse = TestBit(rel, REL_X) && TestBit(rel, REL_Y);
                }

                bool isKeyboard = false;
                if (TestBit(types, EV_KEY))
                {
                    var keys = new byte[96];
                    ioctl(fd, EVIOCGBIT(EV_KEY, keys.Length), keys);
                    isKeyboard = TestBit(keys, 30 /* KEY_A */) && TestBit(keys, 28 /* KEY_ENTER */);
                }

                result.Add(new EvdevDevice(path, name, isMouse, isKeyboard));
            }
            finally
            {
                close(fd);
            }
        }

        return result;
    }
}

/// <summary>Reads raw events from a set of evdev devices, one thread per device.</summary>
public sealed class EvdevReader : IDisposable
{
    private readonly List<Thread> _threads = new();
    private readonly CancellationTokenSource _cts = new();

    public EvdevReader(IEnumerable<string> paths, Action<ushort, ushort, int> onEvent)
    {
        foreach (string path in paths)
        {
            int fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd < 0)
                continue;

            var thread = new Thread(() => ReadLoop(fd, onEvent, _cts.Token))
            {
                IsBackground = true,
                Name = "evdev " + path,
            };
            _threads.Add(thread);
            thread.Start();
        }
    }

    public int DeviceCount => _threads.Count;

    private static void ReadLoop(int fd, Action<ushort, ushort, int> onEvent, CancellationToken ct)
    {
        var fds = new[] { new PollFd { fd = fd, events = POLLIN } };
        var buf = new byte[InputEventSize * 64];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (poll(fds, 1, 200) <= 0)
                    continue; // timeout (lets us notice cancellation) or EINTR

                nint n = read(fd, buf, buf.Length);
                if (n < 0)
                    break; // device unplugged
                if (n == 0)
                    continue;

                for (int off = 0; off + InputEventSize <= n; off += InputEventSize)
                {
                    ushort type = BitConverter.ToUInt16(buf, off + 16);
                    ushort code = BitConverter.ToUInt16(buf, off + 18);
                    int value = BitConverter.ToInt32(buf, off + 20);
                    onEvent(type, code, value);
                }
            }
        }
        finally
        {
            close(fd);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var t in _threads)
            t.Join(500);
        _cts.Dispose();
    }
}
