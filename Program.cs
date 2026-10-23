using Avalonia;
using FortniteAFKXPMonitor.Services;

namespace FortniteAFKXPMonitor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // "FortniteAFKXPMonitor --toggle" etc.: talk to the copy that's already running, then exit.
        if (CommandChannel.TryParse(args, out string command))
            return CommandChannel.RunClient(command);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
