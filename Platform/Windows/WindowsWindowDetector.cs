using System.IO;
using System.Text;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Windows.NativeMethods;

namespace FortniteAFKXPMonitor.Platform.Windows;

public sealed class WindowsWindowDetector : IWindowDetector
{
    public int PollIntervalMs => 5;

    public string? Problem => null;

    public WindowInfo GetForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return WindowInfo.None;

        return new WindowInfo(GetProcessName(hwnd), "", GetTitle(hwnd));
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return "";

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString())
                : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
