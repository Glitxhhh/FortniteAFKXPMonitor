// Everything here is only reached on Linux (PlatformServices / MainWindow guard on OperatingSystem.IsLinux),
// but the assembly is shared with Windows, so silence the "Unix-only API" analyzer for this file.
#pragma warning disable CA1416

using System.Diagnostics;
using FortniteAFKXPMonitor.Services;
using static FortniteAFKXPMonitor.Platform.Linux.LinuxNative;

namespace FortniteAFKXPMonitor.Platform.Linux;

internal static class LinuxTools
{
    public static bool OnPath(string name) => Find(name) is not null;

    public static string? Find(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            try
            {
                if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & anyExec) != 0)
                    return candidate;
            }
            catch
            {
                // unreadable directory entry: keep looking
            }
        }
        return null;
    }
}

public enum SetupState { Ok, Missing, NeedsRelogin, Unsupported }

public sealed record SetupItem(string Id, string Title, SetupState State, string Detail, bool CanFix);

public sealed record SetupResult(bool Success, string Message);

/// <summary>
/// Works out what a Linux install is still missing (input permissions, the focus-detection tool) by looking
/// at the real system state each time, so there's no "setup done" flag that can go stale. Also runs the fixes.
/// </summary>
public static class LinuxSetup
{
    public const string InputId = "input";
    public const string ToolId = "tool";

    // Terminal emulators and how each one wants to be told "run this command".
    private static readonly (string Exe, string[] Prefix)[] Terminals =
    {
        ("konsole", new[] { "-e" }),
        ("gnome-terminal", new[] { "--" }),
        ("xfce4-terminal", new[] { "-x" }),
        ("kitty", Array.Empty<string>()),
        ("alacritty", new[] { "-e" }),
        ("foot", Array.Empty<string>()),
        ("wezterm", new[] { "start", "--" }),
        ("xterm", new[] { "-e" }),
        ("x-terminal-emulator", new[] { "-e" }),
    };

    /// <summary>Safe to call from any thread; cheap enough to poll every few seconds.</summary>
    public static IReadOnlyList<SetupItem> Check(IWindowDetector detector, bool focusCheckEnabled) =>
        new[] { CheckInput(), CheckFocusTool(detector, focusCheckEnabled) };

    // ---- checks -------------------------------------------------------------

    private static SetupItem CheckInput()
    {
        bool uinputOk = false;
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd >= 0)
        {
            close(fd);
            uinputOk = true;
        }

        var devices = EvdevScanner.Scan();
        bool evdevOk = devices.Any(d => d.IsMouse) && devices.Any(d => d.IsKeyboard);

        if (uinputOk && evdevOk)
            return new SetupItem(InputId, "Input access", SetupState.Ok,
                "Can send input and read your mouse and keyboard.", false);

        // Added to the group already, but this session predates it: only a fresh login picks it up.
        if (ReadInputGroup() is { IsMember: true } group && !ProcessHasGroup(group.Gid))
            return new SetupItem(InputId, "Input access", SetupState.NeedsRelogin,
                "You've been added to the 'input' group, but it only applies after you log out and back in. Then start the app again.", false);

        string detail;
        if (!uinputOk && !File.Exists("/dev/uinput"))
            detail = "/dev/uinput doesn't exist (the uinput module isn't loaded, or this kernel was built without it).";
        else if (!uinputOk)
            detail = "Can't open /dev/uinput, which the app needs to send clicks and keys.";
        else
            detail = "Can't read /dev/input, which the app needs for the global hotkey and to notice your real mouse.";

        return new SetupItem(InputId, "Input access", SetupState.Missing, detail, true);
    }

    private static SetupItem CheckFocusTool(IWindowDetector detector, bool focusCheckEnabled)
    {
        if (!focusCheckEnabled)
            return new SetupItem(ToolId, "Focus detection", SetupState.Ok,
                "The focus check is turned off, so no tool is needed.", false);

        string? tool = detector.RequiredTool;

        // It may have been installed while the app was open.
        if (detector.Problem is not null && tool is not null && LinuxTools.OnPath(tool))
            detector.Refresh();

        if (detector.Problem is null)
            return new SetupItem(ToolId, "Focus detection", SetupState.Ok, $"Using {tool}.", false);

        if (tool is null)
            return new SetupItem(ToolId, "Focus detection", SetupState.Unsupported, detector.Problem, false);

        bool installable = tool is "kdotool" or "xdotool";
        string detail = installable
            ? $"'{tool}' isn't installed. The app uses it to tell whether your game is focused."
            : detector.Problem;
        return new SetupItem(ToolId, "Focus detection", SetupState.Missing, detail, installable);
    }

    private static (int Gid, bool IsMember)? ReadInputGroup()
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/group"))
            {
                string[] p = line.Split(':');
                if (p.Length >= 4 && p[0] == "input" && int.TryParse(p[2], out int gid))
                    return (gid, p[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(Environment.UserName));
            }
        }
        catch
        {
            // no /etc/group (unusual): treat as unknown
        }
        return null;
    }

    private static bool ProcessHasGroup(int gid)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Groups:", StringComparison.Ordinal))
                    continue;
                return line["Groups:".Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(g => g == gid.ToString());
            }
        }
        catch
        {
            // fall through
        }
        return false;
    }

    // ---- fixes --------------------------------------------------------------

    /// <summary>
    /// Runs setup-input.sh as root through pkexec (polkit shows the system's password dialog).
    /// The script is embedded in the app and handed to the shell as text, never read from disk at
    /// run time, so nothing on disk can be swapped for it between the prompt and the run.
    /// </summary>
    public static async Task<SetupResult> FixInputAccessAsync()
    {
        string user = Environment.UserName;
        if (user.Length == 0 || user[0] == '-' || !user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return new SetupResult(false, $"Unexpected user name '{user}'. Run setup-input.sh from the release archive instead.");

        string? pkexec = LinuxTools.Find("pkexec");
        if (pkexec is null)
            return new SetupResult(false, "pkexec (polkit) isn't installed. Run this in a terminal instead:  sudo ./setup-input.sh \"$USER\"  (script is in the release archive)");

        var psi = new ProcessStartInfo(pkexec)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ReadResource("setup-input.sh"));
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(user);

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("pkexec didn't start");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            string output = (await stdout + "\n" + await stderr).Trim();

            return p.ExitCode switch
            {
                0 => new SetupResult(true, "Done. Log out and back in so the 'input' group applies, then start the app again."),
                126 or 127 => new SetupResult(false, "Couldn't get authorization - the password dialog was cancelled, or no polkit agent is running."),
                _ => new SetupResult(false, LastLines(output, 3) is { Length: > 0 } tail ? tail : $"Setup failed (exit code {p.ExitCode})."),
            };
        }
        catch (Exception ex)
        {
            return new SetupResult(false, $"Couldn't run setup: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a terminal running install-focus-tool.sh. It needs a real terminal: it asks for your sudo
    /// password and, for AUR packages, shows the PKGBUILD to review. Returns as soon as the terminal opens;
    /// the caller notices completion by re-running <see cref="Check"/>.
    /// </summary>
    public static SetupResult StartFocusToolInstall(string tool)
    {
        if (tool is not ("kdotool" or "xdotool"))
            return new SetupResult(false, $"Don't know how to install '{tool}'.");

        string script;
        try
        {
            string dir = Directory.CreateTempSubdirectory("fortnite-afk-").FullName;   // mode 0700
            script = Path.Combine(dir, "install-focus-tool.sh");
            File.WriteAllText(script, ReadResource("install-focus-tool.sh"));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            return new SetupResult(false, $"Couldn't prepare the installer: {ex.Message}");
        }

        foreach (var (exe, prefix) in Terminals)
        {
            string? path = LinuxTools.Find(exe);
            if (path is null)
                continue;

            var psi = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (string arg in prefix)
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("bash \"$1\" --tool \"$2\"; echo; printf 'Press Enter to close...'; read _");
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(tool);

            try
            {
                Process.Start(psi)?.Dispose();
                return new SetupResult(true, $"A terminal opened ({exe}). Follow the prompts there - this card updates when it's done.");
            }
            catch
            {
                // try the next terminal
            }
        }

        return new SetupResult(false, $"No terminal emulator found. Run this yourself in a terminal:  bash {script} --tool {tool}");
    }

    // ---- helpers ------------------------------------------------------------

    private static string ReadResource(string name)
    {
        using Stream stream = typeof(LinuxSetup).Assembly.GetManifestResourceStream(name)
                              ?? throw new FileNotFoundException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static string LastLines(string text, int count) =>
        string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
}
