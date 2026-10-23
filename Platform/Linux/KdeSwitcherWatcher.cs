// Linux-only, but the assembly is shared with Windows.
#pragma warning disable CA1416

using Tmds.DBus.Protocol;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// Knows when KDE's window switcher (Alt+Tab) is open, without needing to see the Alt key.
///
/// The /dev/input reader only sees physical keyboards, and keys forwarded by Deskflow / Synergy / Input Leap go
/// straight into the compositor, so Alt on such a keyboard is invisible to it. But KDE's shortcut daemon announces
/// every global shortcut the compositor handles, whatever device it came from: "Walk Through Windows" is pressed when
/// Alt+Tab is hit and released when Alt comes up, which brackets the switcher exactly (measured on Plasma 6: released
/// 2.580 s after pressed for Tab-down to Alt-up of 2.58 s).
/// </summary>
internal sealed class KdeSwitcherWatcher : IDisposable
{
    private const string Service = "org.kde.kglobalaccel";
    private const string Component = "/component/kwin";
    private const string ComponentInterface = "org.kde.kglobalaccel.Component";

    // Walk Through Windows, ... (Reverse), ... of Current Application, ... Alternative
    private const string SwitcherPrefix = "Walk Through Windows";

    // If a "released" is ever missed (the daemon restarts mid-switch), don't stay paused forever.
    private const long MaxSwitchMs = 20_000;

    private readonly object _lock = new();
    private readonly HashSet<string> _active = new();
    private long _since;
    private DBusConnection? _connection;
    private IDisposable? _subscription;

    /// <summary>True while a switcher shortcut is held down.</summary>
    public bool Active
    {
        get
        {
            lock (_lock)
            {
                if (_active.Count == 0)
                    return false;
                if (Environment.TickCount64 - _since > MaxSwitchMs)
                {
                    _active.Clear();
                    return false;
                }
                return true;
            }
        }
    }

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            _connection = new DBusConnection(PortalShortcuts.SessionBusAddress());
            await _connection.ConnectAsync();

            _subscription = await _connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Component, Interface = ComponentInterface },
                ReadSignal, OnSignal, false, ObserverFlags.None, null);
        }
        catch (Exception ex)
        {
            // Not KDE (no shortcut daemon) or no session bus: the /dev/input Alt tracking still works.
            Console.Error.WriteLine("[switcher] not watching KDE's window switcher: " + ex.Message);
        }
    }

    private static (string Member, string Shortcut) ReadSignal(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        reader.ReadString();                    // component unique name ("kwin")
        string shortcut = reader.ReadString();  // e.g. "Walk Through Windows"
        return (message.MemberAsString ?? "", shortcut);
    }

    private void OnSignal(Notification<(string Member, string Shortcut)> notification)
    {
        if (!notification.HasValue)
            return;

        var (member, shortcut) = notification.Value;
        if (!shortcut.StartsWith(SwitcherPrefix, StringComparison.Ordinal))
            return;

        lock (_lock)
        {
            if (member == "globalShortcutPressed")
            {
                if (_active.Count == 0)
                    _since = Environment.TickCount64;
                _active.Add(shortcut);
            }
            else if (member == "globalShortcutReleased")
            {
                _active.Remove(shortcut);
            }
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _connection?.Dispose();
    }
}
