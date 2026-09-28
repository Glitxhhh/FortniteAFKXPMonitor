using System.Runtime.InteropServices;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>Thin libc bindings. Only ever called on Linux; the assembly still compiles everywhere.</summary>
internal static class LinuxNative
{
    public const int O_RDONLY = 0;
    public const int O_WRONLY = 1;
    public const int O_NONBLOCK = 0x800;
    public const int O_CLOEXEC = 0x80000;

    public const short POLLIN = 0x001;

    // Event types / codes from <linux/input-event-codes.h>
    public const ushort EV_SYN = 0x00;
    public const ushort EV_KEY = 0x01;
    public const ushort EV_REL = 0x02;
    public const ushort REL_X = 0x00;
    public const ushort REL_Y = 0x01;
    public const ushort BTN_LEFT = 0x110;
    public const ushort BTN_RIGHT = 0x111;
    public const ushort BTN_MIDDLE = 0x112;

    // uinput ioctls
    public const ulong UI_DEV_CREATE = 0x5501;
    public const ulong UI_DEV_DESTROY = 0x5502;
    public const ulong UI_DEV_SETUP = 0x405c5503;
    public const ulong UI_SET_EVBIT = 0x40045564;
    public const ulong UI_SET_KEYBIT = 0x40045565;
    public const ulong UI_SET_RELBIT = 0x40045566;

    /// <summary>Size of struct input_event on 64-bit Linux (16-byte timeval + type + code + value).</summary>
    public const int InputEventSize = 24;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern nint read(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    public static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, int arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, byte[] arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int poll([In, Out] PollFd[] fds, ulong nfds, int timeout);

    // EVIOCGBIT(ev, len) and EVIOCGNAME(len) from <linux/input.h>
    public static ulong EVIOCGBIT(int ev, int len) => 0x80000000UL | ((ulong)len << 16) | (0x45UL << 8) | (ulong)(uint)(0x20 + ev);

    public static ulong EVIOCGNAME(int len) => 0x80000000UL | ((ulong)len << 16) | (0x45UL << 8) | 0x06UL;

    public static bool TestBit(byte[] bits, int bit) =>
        bit / 8 < bits.Length && (bits[bit / 8] & (1 << (bit % 8))) != 0;
}
