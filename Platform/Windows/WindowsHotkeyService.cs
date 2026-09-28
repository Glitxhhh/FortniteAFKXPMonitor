using System.Runtime.InteropServices;
using Avalonia.Input;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Windows.NativeMethods;

namespace FortniteAFKXPMonitor.Platform.Windows;

/// <summary>
/// Global hotkey via a low-level keyboard hook. Unlike RegisterHotKey it needs no window handle,
/// and the key press is swallowed so the game doesn't also receive it. Must be created on the UI thread.
/// </summary>
public sealed class WindowsHotkeyService : IHotkeyService
{
    private readonly LowLevelHookProc _proc;
    private IntPtr _hook;
    private int _vk;
    private bool _down;

    public event Action? Pressed;

    public string? Problem { get; private set; }

    public WindowsHotkeyService() => _proc = HookCallback;

    public bool Register(Key key)
    {
        if (!KeyMap.TryGetVk(key, out int vk))
        {
            Problem = $"{key} can't be used as a hotkey.";
            return false;
        }

        _vk = vk;
        _down = false;

        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Problem = "Couldn't install the global hotkey hook.";
                return false;
            }
        }

        Problem = null;
        return true;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;

            if (!injected && info.vkCode == (uint)_vk)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (!_down)
                    {
                        _down = true;
                        Pressed?.Invoke();
                    }
                    return (IntPtr)1; // swallow
                }
                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _down = false;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
