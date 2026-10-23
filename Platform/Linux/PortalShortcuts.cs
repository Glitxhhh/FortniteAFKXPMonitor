// Linux-only, but the assembly is shared with Windows.
#pragma warning disable CA1416

using Avalonia.Input;
using Avalonia.Platform;
using Tmds.DBus.Protocol;

namespace FortniteAFKXPMonitor.Platform.Linux;

/// <summary>
/// A global hotkey through the XDG desktop portal (org.freedesktop.portal.GlobalShortcuts).
///
/// The /dev/input reader only sees physical keyboards. Keys forwarded by Deskflow / Synergy / Input Leap arrive
/// through libei, i.e. they go straight into the compositor and are never kernel input devices. The compositor
/// does see them, and the portal lets an app ask it to report one key, so this catches them (verified on KDE
/// Plasma 6 with Deskflow). The first time, KDE shows a dialog asking you to confirm the shortcut.
///
/// An unsandboxed app must register an app id first, and the portal only accepts ids that have a matching .desktop
/// file, so <see cref="DesktopEntry"/> writes one for the current user.
/// </summary>
internal sealed class PortalShortcuts : IDisposable
{
    public const string AppId = "org.glitxh.FortniteAFKXPMonitor";

    private const string ShortcutId = "toggle";
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    private delegate void WriteBody(ref MessageWriter writer);

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _subscriptions = new();
    private DBusConnection? _connection;
    private string? _session;
    private int _tokens;

    /// <summary>Raised (on a thread-pool thread) each time the shortcut is pressed.</summary>
    public event Action? Activated;

    /// <summary>Human-readable state, for showing in the UI.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Console.Error.WriteLine("[portal] " + value);   // visible in the service log / terminal for troubleshooting
        }
    }

    private string _status = "Starting...";

    /// <summary>True once the portal has bound the shortcut (even if the user hasn't assigned it a key).</summary>
    public bool IsBound { get; private set; }

    public void Start(Key key)
    {
        string? trigger = PortalTrigger(key);
        _ = Task.Run(() => RunAsync(trigger));
    }

    private async Task RunAsync(string? preferredTrigger)
    {
        try
        {
            Status = "Connecting to the desktop portal...";
            DesktopEntry.EnsureInstalled();

            _connection = new DBusConnection(SessionBusAddress());
            await _connection.ConnectAsync();
            Trace($"connected as {_connection.UniqueName}");

            // Must be the first portal call on this connection, and only works for ids with a .desktop file.
            await CallAsync("org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter w) =>
            {
                w.WriteString(AppId);
                var options = w.WriteDictionaryStart();
                w.WriteDictionaryEnd(options);
            });

            Trace("app id registered");
            _subscriptions.Add(await _connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = ShortcutsInterface, Member = "Activated", Path = PortalPath },
                ReadActivated, OnActivated, false, ObserverFlags.None, null));

            Trace("listening for Activated");
            _session = await CreateSessionAsync();
            Trace($"session {_session}");
            await BindAsync(_session, preferredTrigger);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[portal] failed: " + ex);
            Status = Describe(ex);
        }
    }

    /// <summary>
    /// Changes the key the desktop has bound to the shortcut, for when the user picks a new hotkey in the app.
    /// The portal itself can't do this (once an id is known it keeps the user's key and ignores our preference), so on
    /// KDE this makes the same call System Settings uses to change another app's shortcut: no dialog, no editor.
    /// Other desktops ignore it and keep their own key (change it in their shortcut settings).
    /// </summary>
    public void Rebind(Key key)
    {
        string? trigger = PortalTrigger(key);
        int? qtKey = QtKey(key);
        if (!IsBound || _connection is null || trigger is null || qtKey is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await CallAsync("org.kde.KGlobalAccel", "setForeignShortcut", "asai", (ref MessageWriter w) =>
                {
                    // actionId = [component, action, component's display name, action's display name]
                    w.WriteArray(new[] { AppId, ShortcutId, "Fortnite AFK XP Tool", "Start or stop the AFK clicker" });
                    w.WriteArray(new[] { qtKey.Value });
                }, destination: "org.kde.kglobalaccel", path: "/kglobalaccel");

                Status = $"Also bound through your desktop's global shortcuts: {trigger} (change it in System Settings > Shortcuts)";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[portal] couldn't change the desktop shortcut: " + ex.Message);
                Status = "The desktop shortcut keeps its old key (this desktop can't be changed from the app). Change it in your desktop's shortcut settings.";
            }
        });
    }

    // ---- portal calls -----------------------------------------------------------------------------

    private async Task<string> CreateSessionAsync()
    {
        Status = "Asking the desktop for a shortcut session...";
        string token = NextToken();
        var (code, results) = await RequestAsync("CreateSession", "a{sv}", token, TimeSpan.FromSeconds(15), (ref MessageWriter w) =>
        {
            var options = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart(); w.WriteString("handle_token"); w.WriteVariantString(token);
            w.WriteDictionaryEntryStart(); w.WriteString("session_handle_token"); w.WriteVariantString("afk" + token);
            w.WriteDictionaryEnd(options);
        });

        if (code != 0 || !results.TryGetValue("session_handle", out var handle))
            throw new InvalidOperationException("The desktop refused to start a shortcut session.");
        return handle.GetString();
    }

    private async Task BindAsync(string session, string? preferredTrigger)
    {
        Status = "Waiting for you to confirm the shortcut in the dialog your desktop shows (first time only)...";
        string token = NextToken();
        var (code, results) = await RequestAsync("BindShortcuts", "oa(sa{sv})sa{sv}", token, TimeSpan.FromMinutes(10), (ref MessageWriter w) =>
        {
            w.WriteObjectPath(session);

            var shortcuts = w.WriteArrayStart(DBusType.Struct);
            w.WriteStructureStart();
            w.WriteString(ShortcutId);
            var properties = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart(); w.WriteString("description"); w.WriteVariantString("Start or stop the AFK clicker");
            if (preferredTrigger is not null)
            {
                w.WriteDictionaryEntryStart(); w.WriteString("preferred_trigger"); w.WriteVariantString(preferredTrigger);
            }
            w.WriteDictionaryEnd(properties);
            w.WriteArrayEnd(shortcuts);

            w.WriteString("");   // parent_window
            var options = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart(); w.WriteString("handle_token"); w.WriteVariantString(token);
            w.WriteDictionaryEnd(options);
        });

        if (code != 0)
        {
            Status = "The shortcut wasn't confirmed, so the global hotkey only works from keyboards the app can read directly.";
            return;
        }

        IsBound = true;
        string trigger = TriggerDescription(results);
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        Status = trigger.Length > 0
            ? $"Also bound through your desktop's global shortcuts: {trigger} (change it in System Settings > Shortcuts)"
            : desktop.Contains("Hyprland", StringComparison.OrdinalIgnoreCase)
                ? $"Registered with Hyprland. Add this line to hyprland.conf to assign a key:  bind = , F6, global, {AppId}:{ShortcutId}"
                : "Registered with your desktop, but no key is assigned. Set one in your desktop's shortcut settings (KDE: System Settings > Shortcuts > Fortnite AFK XP Tool).";
    }

    /// <summary>Pulls the first shortcut's "trigger_description" out of a BindShortcuts response.</summary>
    private static string TriggerDescription(Dictionary<string, VariantValue> results)
    {
        try
        {
            if (!results.TryGetValue("shortcuts", out var list) || list.Count == 0)
                return "";

            VariantValue properties = Unwrap(Unwrap(list.GetItem(0)).GetItem(1));
            for (int i = 0; i < properties.Count; i++)
            {
                var entry = properties.GetDictionaryEntry(i);
                if (Unwrap(entry.Key).GetString() == "trigger_description")
                    return Unwrap(entry.Value).GetString();
            }
        }
        catch
        {
            // The shortcut is bound either way; we just can't show its key.
        }
        return "";
    }

    private static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    // ---- D-Bus plumbing ---------------------------------------------------------------------------

    private async Task CallAsync(string iface, string member, string signature, WriteBody writeBody,
        string destination = Portal, string path = PortalPath)
    {
        MessageBuffer message = BuildCall(iface, member, signature, writeBody, destination, path);
        await _connection!.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(15), _cts.Token);
    }

    /// <summary>
    /// Calls a portal method that answers asynchronously through a Request object's Response signal. The signal
    /// listener is installed before the call (matched on the request token) so a fast answer can't be missed.
    /// </summary>
    private async Task<(uint Code, Dictionary<string, VariantValue> Results)> RequestAsync(
        string member, string signature, string token, TimeSpan timeout, WriteBody writeBody)
    {
        var done = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The portal answers on /org/freedesktop/portal/desktop/request/<our unique name, ':' dropped and '.' -> '_'>/<token>.
        string sender = _connection!.UniqueName!.TrimStart(':').Replace('.', '_');
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        string suffix = "/" + token;

        using IDisposable subscription = await _connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Interface = RequestInterface,
                Member = "Response",
                Path = requestPath,
            },
            ReadResponse,
            (Notification<(string Path, uint Code, Dictionary<string, VariantValue> Results)> n) =>
            {
                // Notification<T>.Exception throws unless this is a completion, so check HasValue first.
                if (n.HasValue)
                {
                    if (n.Value.Path.EndsWith(suffix, StringComparison.Ordinal))
                        done.TrySetResult((n.Value.Code, n.Value.Results));
                }
                else if (n.IsCompletion)
                {
                    done.TrySetException(n.Exception ?? new InvalidOperationException("The D-Bus connection closed."));
                }
            },
            false, ObserverFlags.None, null);

        Trace($"{member}: listening on {requestPath}");
        MessageBuffer message = BuildCall(ShortcutsInterface, member, signature, writeBody);
        await _connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(15), _cts.Token);
        Trace($"{member}: call returned, waiting for the Response signal");

        return await done.Task.WaitAsync(timeout, _cts.Token);
    }

    private MessageBuffer BuildCall(string iface, string member, string signature, WriteBody writeBody,
        string destination = Portal, string path = PortalPath)
    {
        var writer = _connection!.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, member, signature);
            writeBody(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static (string Path, uint Code, Dictionary<string, VariantValue> Results) ReadResponse(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        uint code = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        return (message.PathAsString ?? "", code, results);
    }

    private static (string Session, string Id) ReadActivated(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        string session = reader.ReadObjectPathAsString();
        string id = reader.ReadString();
        return (session, id);
    }

    private void OnActivated(Notification<(string Session, string Id)> n)
    {
        if (n.HasValue && n.Value.Id == ShortcutId && n.Value.Session == _session)
            Activated?.Invoke();
    }

    private static void Trace(string message) => Console.Error.WriteLine("[portal] " + message);

    private string NextToken() => $"afk{Environment.ProcessId}_{Interlocked.Increment(ref _tokens)}";

    internal static string SessionBusAddress()
    {
        string? address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(address))
            return address;

        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{Environment.UserName}";
        return "unix:path=" + Path.Combine(runtime, "bus");
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "The desktop didn't answer the global-shortcut request, so only keyboards the app can read directly work for the hotkey.",
        DBusErrorReplyException d when d.ErrorName.Contains("UnknownMethod") || d.ErrorName.Contains("ServiceUnknown") || d.ErrorName.Contains("UnknownObject")
            => "This desktop has no global-shortcut portal, so only keyboards the app can read directly work for the hotkey (use --toggle from a desktop shortcut for others).",
        _ => $"Global shortcut unavailable ({ex.Message}). Only keyboards the app can read directly work for the hotkey.",
    };

    /// <summary>The Qt::Key value KDE's shortcut daemon expects, or null for keys we don't map.</summary>
    private static int? QtKey(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return 0x01000030 + (key - Key.F1);
        if (key is >= Key.A and <= Key.Z) return 'A' + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return '0' + (key - Key.D0);
        return key switch
        {
            Key.Space => 0x20,
            Key.Escape => 0x01000000,
            Key.Tab => 0x01000001,
            Key.Return => 0x01000004,
            Key.Insert => 0x01000006,
            Key.Delete => 0x01000007,
            Key.Home => 0x01000010,
            Key.End => 0x01000011,
            Key.Left => 0x01000012,
            Key.Up => 0x01000013,
            Key.Right => 0x01000014,
            Key.Down => 0x01000015,
            Key.PageUp => 0x01000016,
            Key.PageDown => 0x01000017,
            _ => null,
        };
    }

    /// <summary>The key as the XDG shortcuts spec writes it (xkb names), or null to let the user pick in the dialog.</summary>
    private static string? PortalTrigger(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key switch
        {
            Key.Space => "space",
            Key.Return => "Return",
            Key.Escape => "Escape",
            Key.Tab => "Tab",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.PageUp => "Prior",
            Key.PageDown => "Next",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        _connection?.Dispose();   // closes our portal session too
        _cts.Dispose();
    }
}

/// <summary>
/// The portal only accepts app ids that have a matching .desktop file, so write one for the current user
/// (no root needed). It also gives the app a launcher entry and the name KDE shows in its shortcut dialog.
/// </summary>
internal static class DesktopEntry
{
    public static void EnsureInstalled()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Can't work out where the app is installed.");

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        string iconPath = WriteIcon(dataHome);
        string applications = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(applications);

        string content =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Fortnite AFK XP Tool\n" +
            "Comment=AFK XP helper\n" +
            $"Exec={ExecQuote(exe)}\n" +
            (iconPath.Length > 0 ? $"Icon={iconPath}\n" : "") +
            "Terminal=false\n" +
            "Categories=Utility;\n";

        string path = Path.Combine(applications, PortalShortcuts.AppId + ".desktop");
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);   // rewritten when the app moves, so Exec stays right
    }

    private static string WriteIcon(string dataHome)
    {
        try
        {
            string dir = Path.Combine(dataHome, "FortniteAFKXPMonitor");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "icon.png");
            if (!File.Exists(path))
            {
                using Stream icon = AssetLoader.Open(new Uri("avares://FortniteAFKXPMonitor/Assets/Icon.png"));
                using FileStream file = File.Create(path);
                icon.CopyTo(file);
            }
            return path;
        }
        catch
        {
            return "";   // the entry works without an icon
        }
    }

    /// <summary>Quotes an executable path for the Exec key (desktop entry spec: wrap in quotes, escape the specials).</summary>
    private static string ExecQuote(string path)
    {
        var escaped = new System.Text.StringBuilder("\"");
        foreach (char c in path)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            if (c == '%') escaped.Append('%');
            escaped.Append(c);
        }
        return escaped.Append('"').ToString();
    }
}
