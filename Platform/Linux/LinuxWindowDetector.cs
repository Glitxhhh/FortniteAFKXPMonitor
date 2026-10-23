using System.Diagnostics;
using System.Text.Json;
using FortniteAFKXPMonitor.Services;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Wayland has no generic "which window is focused" API, so this picks a provider for the running
/// desktop: Hyprland (hyprctl), Sway (swaymsg), niri (niri msg), KDE Plasma on Wayland (kdotool) or any X11
/// session (xdotool).
/// GNOME on Wayland exposes nothing usable and is reported as unsupported.
/// </summary>
public sealed class LinuxWindowDetector : IWindowDetector
{
    private volatile Func<WindowInfo>? _provider;

    public int PollIntervalMs { get; private set; }

    public string? Problem { get; private set; }

    public string? RequiredTool { get; private set; }

    public LinuxWindowDetector() => Detect();

    public void Refresh() => Detect();

    private void Detect()
    {
        string session = Env("XDG_SESSION_TYPE").ToLowerInvariant();
        string desktop = Env("XDG_CURRENT_DESKTOP");
        bool wayland = session == "wayland" || Env("WAYLAND_DISPLAY").Length > 0;

        Func<WindowInfo>? provider = null;
        string? problem = null;
        string? tool = null;
        int poll = 250;

        if (Env("HYPRLAND_INSTANCE_SIGNATURE").Length > 0)
        {
            tool = "hyprctl";
            poll = 150;
            if (LinuxTools.OnPath(tool)) provider = Hyprland;
            else problem = "hyprctl ships with Hyprland but isn't on your PATH.";
        }
        else if (Env("SWAYSOCK").Length > 0)
        {
            tool = "swaymsg";
            poll = 150;
            if (LinuxTools.OnPath(tool)) provider = Sway;
            else problem = "swaymsg ships with Sway but isn't on your PATH.";
        }
        else if (Env("NIRI_SOCKET").Length > 0)
        {
            tool = "niri";
            poll = 150;
            if (LinuxTools.OnPath(tool)) provider = Niri;
            else problem = "niri ships with the niri compositor but isn't on your PATH.";
        }
        else if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) && wayland)
        {
            tool = "kdotool";
            if (LinuxTools.OnPath(tool)) provider = Kde;
            else problem = "KDE Wayland focus detection needs 'kdotool'.";
        }
        else if (!wayland && Env("DISPLAY").Length > 0)
        {
            tool = "xdotool";
            if (LinuxTools.OnPath(tool)) provider = X11;
            else problem = "X11 focus detection needs 'xdotool'.";
        }
        else
        {
            problem = "Focus detection isn't supported on this desktop (GNOME/other Wayland). "
                      + "Turn off \"only send input while the game is focused\", or use KDE, Hyprland, Sway or X11.";
        }

        RequiredTool = tool;
        Problem = problem;
        PollIntervalMs = poll;
        _provider = provider;
    }

    public WindowInfo GetForeground()
    {
        try
        {
            return _provider?.Invoke() ?? WindowInfo.None;
        }
        catch
        {
            return WindowInfo.None;
        }
    }

    // ---- providers --------------------------------------------------------

    private static WindowInfo Hyprland()
    {
        string? json = Run("hyprctl", "activewindow -j");
        if (string.IsNullOrWhiteSpace(json)) return WindowInfo.None;

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string cls = Str(r, "class");
        string title = Str(r, "title");
        int pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out int v) ? v : 0;
        return new WindowInfo(ProcessName(pid), cls, title);
    }

    private static WindowInfo Sway()
    {
        string? json = Run("swaymsg", "-t get_tree");
        if (string.IsNullOrWhiteSpace(json)) return WindowInfo.None;

        using var doc = JsonDocument.Parse(json);
        var focused = FindFocused(doc.RootElement);
        if (focused is null) return WindowInfo.None;

        var n = focused.Value;
        string cls = Str(n, "app_id");
        if (cls.Length == 0 && n.TryGetProperty("window_properties", out var wp))
            cls = Str(wp, "class"); // XWayland windows
        int pid = n.TryGetProperty("pid", out var p) && p.TryGetInt32(out int v) ? v : 0;
        return new WindowInfo(ProcessName(pid), cls, Str(n, "name"));
    }

    private static WindowInfo Niri() => ParseNiri(Run("niri", "msg --json focused-window"));

    private static WindowInfo ParseNiri(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return WindowInfo.None;

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        // The reply is the window object, or `null` when nothing is focused. Also accept the wrapped form the
        // raw IPC socket uses ({"Ok":{"FocusedWindow":{...}}}) in case a future version prints that.
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("Ok", out var ok)) r = ok;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("FocusedWindow", out var fw)) r = fw;
        if (r.ValueKind != JsonValueKind.Object) return WindowInfo.None;

        // title, app_id and pid are all optional in niri's IPC (pid is missing for some portal windows).
        int pid = r.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : 0;
        return new WindowInfo(ProcessName(pid), Str(r, "app_id"), Str(r, "title"));
    }

    private static JsonElement? FindFocused(JsonElement node)
    {
        if (node.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True
            && node.TryGetProperty("pid", out _))
            return node;

        foreach (string key in new[] { "nodes", "floating_nodes" })
        {
            if (!node.TryGetProperty(key, out var children)) continue;
            foreach (var child in children.EnumerateArray())
            {
                var hit = FindFocused(child);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    // kdotool talks to KWin over D-Bus, so every call is slow. Class and pid never change for a given
    // window, so they're cached per window id and only the title (which browser tabs change) is re-read.
    private string _kdeId = "";
    private string _kdeClass = "";
    private string _kdePid = "";

    private WindowInfo Kde()
    {
        string? id = Run("kdotool", "getactivewindow")?.Trim();
        if (string.IsNullOrEmpty(id)) return WindowInfo.None;

        if (id != _kdeId)
        {
            _kdeClass = Run("kdotool", $"getwindowclassname {id}")?.Trim() ?? "";
            int.TryParse(Run("kdotool", $"getwindowpid {id}")?.Trim(), out int pid);
            _kdePid = ProcessName(pid);
            _kdeId = id;
        }

        string title = Run("kdotool", $"getwindowname {id}")?.Trim() ?? "";
        return new WindowInfo(_kdePid, _kdeClass, title);
    }

    private static WindowInfo X11()
    {
        string? id = Run("xdotool", "getactivewindow")?.Trim();
        if (string.IsNullOrEmpty(id)) return WindowInfo.None;

        string cls = Run("xdotool", $"getwindowclassname {id}")?.Trim() ?? "";
        string title = Run("xdotool", $"getwindowname {id}")?.Trim() ?? "";
        int.TryParse(Run("xdotool", $"getwindowpid {id}")?.Trim(), out int pid);
        return new WindowInfo(ProcessName(pid), cls, title);
    }

    // ---- helpers ----------------------------------------------------------

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ProcessName(int pid)
    {
        if (pid <= 0) return "";
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch { return ""; }
    }

    /// <summary>Runs a tool and returns stdout, or null if it isn't installed / failed / timed out.</summary>
    private static string? Run(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(800))
            {
                try { p.Kill(); } catch { /* already gone */ }
                return null;
            }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
    }
}
