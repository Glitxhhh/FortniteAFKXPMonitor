using System.Diagnostics;
using System.IO;
using FortniteAFKXPMonitor.Platform.Linux;
using FortniteAFKXPMonitor.Platform.Windows;

namespace FortniteAFKXPMonitor.Services;

public sealed record ShortcutResult(bool Success, string Message);

/// <summary>Puts the app in the Start menu (Windows) or the app launcher / search (Linux), replacing an older entry.</summary>
public static class AppShortcut
{
    public static string ButtonText => OperatingSystem.IsWindows() ? "Add to Start menu" : "Add to app launcher";

    public static ShortcutResult Install()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return new ShortcutResult(false, "Can't work out where the app is installed.");

            if (OperatingSystem.IsWindows())
                return WindowsShortcut.Install(exe);
            if (OperatingSystem.IsLinux())
                return InstallLinux();

            return new ShortcutResult(false, "Shortcuts aren't supported on this system.");
        }
        catch (Exception ex)
        {
            return new ShortcutResult(false, $"Couldn't create the shortcut: {ex.Message}");
        }
    }

    private static ShortcutResult InstallLinux()
    {
        DesktopEntryResult entry = DesktopEntry.EnsureInstalled();
        RefreshDesktopDatabase(Path.GetDirectoryName(entry.Path)!);

        string message = entry.PreviousExec is null
            ? $"Added to your app launcher ({entry.Path}). It can take a moment to show up in search."
            : entry.Changed && !string.Equals(entry.PreviousExec, Environment.ProcessPath, StringComparison.Ordinal)
                ? $"Replaced the old launcher entry (it pointed at {entry.PreviousExec})."
                : "The launcher entry was already there; it's refreshed.";
        return new ShortcutResult(true, message);
    }

    /// <summary>Some desktops only notice a new .desktop file after this; harmless if the tool isn't installed.</summary>
    private static void RefreshDesktopDatabase(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("update-desktop-database", $"\"{directory}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            process?.WaitForExit(3000);
        }
        catch
        {
            // not installed
        }
    }
}
