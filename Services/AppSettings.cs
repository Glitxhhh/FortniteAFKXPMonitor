using System.IO;
using System.Text.Json;

namespace FortniteAFKXPMonitor.Services;

public enum ActionKind { Mouse, Key }

public enum ClickMode { Auto, Hold }

public sealed class AppSettings
{
    public ActionKind Action { get; set; } = ActionKind.Mouse;
    public ClickMode Mode { get; set; } = ClickMode.Auto;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;

    /// <summary>Windows virtual-key code of the key to tap/hold (default Space).</summary>
    public int KeyVk { get; set; } = 0x20;

    /// <summary>Delay between taps/clicks in Auto mode.</summary>
    public int IntervalMs { get; set; } = 100;

    /// <summary>Average minutes between camera nudges. Fortnite's AFK timer is 7 minutes.</summary>
    public double NudgeMinutes { get; set; } = 5;

    public int NudgePixels { get; set; } = 60;

    /// <summary>Only send input while Fortnite / GeForce NOW / GeForce Infinity is the focused window.</summary>
    public bool RequireTarget { get; set; } = true;

    /// <summary>Global start/stop hotkey (default F6).</summary>
    public int HotkeyVk { get; set; } = 0x75;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FortniteAFKXPMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: settings just won't persist.
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
