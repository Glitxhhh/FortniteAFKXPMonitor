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

        // Started by an update: let the old copy finish exiting first, then tidy up what it left behind.
        int waitAt = Array.IndexOf(args, "--wait-for");
        if (waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out int pid))
            UpdateService.WaitForExit(pid);
        UpdateService.CleanupAfterUpdate();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
