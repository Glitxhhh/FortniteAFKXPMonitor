using System.IO;
using System.Text;
using static FortniteAFKXPMonitor.Services.NativeMethods;

namespace FortniteAFKXPMonitor.Services;

public sealed record TargetInfo(bool IsTarget, string Label, string ProcessName);

/// <summary>Works out whether the foreground window is Fortnite, GeForce NOW or GeForce Infinity.</summary>
public static class TargetDetector
{
    // Browsers only count when the tab title looks like a cloud-gaming / Fortnite session.
    private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera" };
    private static readonly string[] BrowserTitleHints = { "geforce now", "xbox", "fortnite", "luna" };

    public static TargetInfo GetForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return new TargetInfo(false, "None", "");

        string process = GetProcessName(hwnd);
        if (process.Length == 0)
            return new TargetInfo(false, "Unknown", "");

        string exe = process.ToLowerInvariant();

        if (exe == "fortniteclient-win64-shipping")
            return new TargetInfo(true, "Fortnite", process);

        if (exe == "geforcenow")
            return new TargetInfo(true, "GeForce NOW", process);

        // GeForce Infinity is an Electron app whose executable is GeForceInfinity.exe.
        if (exe == "geforceinfinity" || exe.Contains("geforce") && exe.Contains("infinity"))
            return new TargetInfo(true, "GeForce Infinity", process);

        // Xbox app (streams Xbox Cloud Gaming). Its window can be reported under the UWP frame host.
        if (exe == "xboxpcapp")
            return new TargetInfo(true, "Xbox Cloud Gaming", process);

        if (exe == "applicationframehost" && GetTitle(hwnd).Contains("xbox", StringComparison.OrdinalIgnoreCase))
            return new TargetInfo(true, "Xbox Cloud Gaming", "XboxPcApp");

        if (Array.IndexOf(Browsers, exe) >= 0)
        {
            string title = GetTitle(hwnd).ToLowerInvariant();
            foreach (string hint in BrowserTitleHints)
            {
                if (title.Contains(hint))
                    return new TargetInfo(true, hint switch
                    {
                        "luna" => "Amazon Luna (browser)",
                        "xbox" => "Xbox Cloud Gaming (browser)",
                        _ => "GeForce NOW (browser)",
                    }, process);
            }
        }

        return new TargetInfo(false, process, process);
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
