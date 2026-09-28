using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

namespace FortniteAFKXPMonitor.Services;

public enum ActionKind { Mouse, Key }

public enum RepeatMode { Auto, Hold }

public sealed class AppSettings
{
    public ActionKind Action { get; set; } = ActionKind.Mouse;
    public RepeatMode Mode { get; set; } = RepeatMode.Auto;
    public MouseButtonKind MouseButton { get; set; } = MouseButtonKind.Left;

    /// <summary>Key to tap/hold (default Space).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Key Key { get; set; } = Key.Space;

    /// <summary>Delay between taps/clicks in Auto mode.</summary>
    public int IntervalMs { get; set; } = 100;

    /// <summary>Average minutes between camera nudges. Fortnite's AFK timer is 7 minutes.</summary>
    public double NudgeMinutes { get; set; } = 5;

    public int NudgePixels { get; set; } = 60;

    /// <summary>Only send input while Fortnite / GeForce NOW / GeForce Infinity is the focused window.</summary>
    public bool RequireTarget { get; set; } = true;

    /// <summary>Global start/stop hotkey (default F6).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Key Hotkey { get; set; } = Key.F6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FortniteAFKXPMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Non-fatal: settings just won't persist.
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
