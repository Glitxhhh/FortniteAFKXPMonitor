using System.Runtime.InteropServices;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Windows.NativeMethods;

namespace FortniteAFKXPMonitor.Platform.Windows;

/// <summary>
/// Low-level mouse hook that reports movement from a real mouse. Input injected by this app
/// (or any SendInput caller) carries the injected flag and is ignored, so our own camera
/// nudges never count. Must be started on a thread with a message loop (the UI thread).
/// </summary>
public sealed class WindowsMouseMonitor : IMouseActivityMonitor
{
    // Ignore tiny jitter; a real camera move adds up to this many pixels quickly.
    private const int MinDistance = 4;

    private readonly LowLevelHookProc _proc;   // kept in a field so the GC can't collect the delegate
    private IntPtr _hook;
    private int _lastX, _lastY;
    private bool _hasLast;
    private int _accum;

    public event Action? UserMoved;

    public string? Problem => null;

    public WindowsMouseMonitor() => _proc = HookCallback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hasLast = false;
        _accum = 0;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;

            if (!injected)
            {
                if (_hasLast)
                {
                    _accum += Math.Abs(info.x - _lastX) + Math.Abs(info.y - _lastY);
                    if (_accum >= MinDistance)
                    {
                        _accum = 0;
                        UserMoved?.Invoke();
                    }
                }
                _lastX = info.x;
                _lastY = info.y;
                _hasLast = true;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
