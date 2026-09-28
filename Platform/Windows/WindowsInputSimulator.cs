using System.Runtime.InteropServices;
using Avalonia.Input;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Windows.NativeMethods;

namespace FortniteAFKXPMonitor.Platform.Windows;

/// <summary>SendInput wrapper. Keys use hardware scan codes so games that ignore virtual-key input still see them.</summary>
public sealed class WindowsInputSimulator : IInputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public string? Problem => null;

    public void MouseDown(MouseButtonKind b) => SendMouse(b switch
    {
        MouseButtonKind.Right => MOUSEEVENTF_RIGHTDOWN,
        MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN,
    });

    public void MouseUp(MouseButtonKind b) => SendMouse(b switch
    {
        MouseButtonKind.Right => MOUSEEVENTF_RIGHTUP,
        MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEUP,
        _ => MOUSEEVENTF_LEFTUP,
    });

    public void MouseMoveRelative(int dx, int dy)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
        Send(input);
    }

    public void KeyDown(Key key) => SendKey(key, false);

    public void KeyUp(Key key) => SendKey(key, true);

    public void Dispose()
    {
    }

    private static void SendMouse(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dwFlags = flags };
        Send(input);
    }

    private static void SendKey(Key key, bool up)
    {
        if (!KeyMap.TryGetVk(key, out int vk))
            return;

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
