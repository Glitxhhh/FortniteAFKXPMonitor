namespace FortniteAFKXPMonitor.Services;

/// <summary>
/// Decides whether the focused window is a supported game / cloud-gaming client.
/// Names are normalised (lower-case, no punctuation) so "GeForceInfinity", "geforce-infinity" and
/// "GeForce Infinity" all compare equal across Windows process names and Linux window classes.
/// </summary>
public static class TargetMatcher
{
    // Browsers only count when the tab title looks like a cloud-gaming / Fortnite session.
    private static readonly string[] Browsers =
        { "chrome", "chromium", "msedge", "microsoftedge", "firefox", "brave", "opera", "vivaldi" };

    private static readonly string[] BrowserTitleHints = { "geforce now", "xbox", "fortnite", "luna" };

    public static TargetInfo Match(WindowInfo w)
    {
        string process = Normalize(w.Process);
        string cls = Normalize(w.Class);
        string display = w.Process.Length > 0 ? w.Process : w.Class.Length > 0 ? w.Class : "Unknown";

        if (process.Length == 0 && cls.Length == 0)
            return new TargetInfo(false, "None", "");

        bool Is(string name) => process == name || cls == name;
        bool Has(string part) => process.Contains(part) || cls.Contains(part);

        if (process.StartsWith("fortniteclient") || cls.StartsWith("fortniteclient"))
            return new TargetInfo(true, "Fortnite", display);

        if (Is("geforcenow"))
            return new TargetInfo(true, "GeForce NOW", display);

        // GeForce Infinity is an Electron app (GeForceInfinity.exe / geforce-infinity).
        if (Is("geforceinfinity") || Has("geforce") && Has("infinity"))
            return new TargetInfo(true, "GeForce Infinity", display);

        // Xbox app (streams Xbox Cloud Gaming). On Windows its window can be reported under the UWP frame host.
        if (Is("xboxpcapp"))
            return new TargetInfo(true, "Xbox Cloud Gaming", display);
        if (Is("applicationframehost") && w.Title.Contains("xbox", StringComparison.OrdinalIgnoreCase))
            return new TargetInfo(true, "Xbox Cloud Gaming", "XboxPcApp");

        if (Browsers.Any(Has))
        {
            string title = w.Title.ToLowerInvariant();
            foreach (string hint in BrowserTitleHints)
            {
                if (title.Contains(hint))
                {
                    return new TargetInfo(true, hint switch
                    {
                        "luna" => "Amazon Luna (browser)",
                        "xbox" => "Xbox Cloud Gaming (browser)",
                        _ => "GeForce NOW (browser)",
                    }, display);
                }
            }
        }

        return new TargetInfo(false, display, display);
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
