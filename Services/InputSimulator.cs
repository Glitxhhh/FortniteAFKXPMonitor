using System.Runtime.InteropServices;
using static FortniteAFKXPMonitor.Services.NativeMethods;

namespace FortniteAFKXPMonitor.Services;

public enum MouseButtonKind { Left, Right, Middle }

/// <summary>SendInput wrapper. Keys use hardware scan codes so games that ignore virtual-key input still see them.</summary>
public static class InputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public static void MouseDown(MouseButtonKind b) => SendMouse(b switch
    {
        MouseButtonKind.Right => MOUSEEVENTF_RIGHTDOWN,
        MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN,
    });

    public static void MouseUp(MouseButtonKind b) => SendMouse(b switch
    {
        MouseButtonKind.Right => MOUSEEVENTF_RIGHTUP,
        MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEUP,
        _ => MOUSEEVENTF_LEFTUP,
    });

    public static void MouseMoveRelative(int dx, int dy)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
        Send(input);
    }

    public static void KeyDown(int vk) => SendKey(vk, false);

    public static void KeyUp(int vk) => SendKey(vk, true);

    private static void SendMouse(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dwFlags = flags };
        Send(input);
    }

    private static void SendKey(int vk, bool up)
    {
        uint mapped = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        var input = new INPUT { type = INPUT_KEYBOARD };
        uint flags = up ? KEYEVENTF_KEYUP : 0;

        if (mapped != 0)
        {
            flags |= KEYEVENTF_SCANCODE;
            if ((mapped & 0xFF00) == 0xE000)
                flags |= KEYEVENTF_EXTENDEDKEY;
            input.u.ki = new KEYBDINPUT { wScan = (ushort)(mapped & 0xFF), dwFlags = flags };
        }
        else
        {
            input.u.ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags };
        }

        Send(input);
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, InputSize);
}
