using System.IO;
using System.Runtime.Versioning;
using FortniteAFKXPMonitor.Services;

namespace FortniteAFKXPMonitor.Platform.Windows;

/// <summary>
/// A Start menu shortcut, which is also what makes the app show up in Windows search.
/// Creating a .lnk needs COM; the Windows Script Host object does it without any extra package.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShortcut
{
    private const string ShortcutName = "Fortnite AFK XP Tool.lnk";

    /// <param name="programsDir">The Start menu "Programs" folder (overridable so it can be tested without touching the real one).</param>
    public static ShortcutResult Install(string exePath, string? programsDir = null)
    {
        programsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programsDir);

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return new ShortcutResult(false, "Windows Script Host isn't available here, so the shortcut can't be created.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        string ours = Path.Combine(programsDir, ShortcutName);
        string exeName = Path.GetFileName(exePath);
        string? movedFrom = null;   // where an existing shortcut used to point, if it pointed somewhere else
        bool existed = false;

        // Older versions and other copies: any shortcut in the folder that launches an exe with our name.
        foreach (string lnk in Directory.EnumerateFiles(programsDir, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string target;
            try { target = shell.CreateShortcut(lnk).TargetPath; }
            catch { continue; }

            if (!string.Equals(Path.GetFileName(target), exeName, StringComparison.OrdinalIgnoreCase))
                continue;

            existed = true;
            if (!string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase))
                movedFrom ??= target;
            if (!string.Equals(lnk, ours, StringComparison.OrdinalIgnoreCase))
                File.Delete(lnk);   // an older shortcut under another name: replaced by ours
        }

        dynamic link = shell.CreateShortcut(ours);
        link.TargetPath = exePath;
        link.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
        link.Description = "Fortnite AFK XP Tool";
        link.IconLocation = exePath + ",0";
        link.Save();

        string message = movedFrom is not null
            ? $"Replaced the old Start menu shortcut (it pointed at {movedFrom}). Search for \"Fortnite AFK XP Tool\"."
            : existed
                ? "The Start menu shortcut was already there; it's refreshed."
                : "Added to the Start menu. Search for \"Fortnite AFK XP Tool\" to launch it.";
        return new ShortcutResult(true, message);
    }
}
