using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FortniteAFKXPMonitor.Platform.Linux;
using FortniteAFKXPMonitor.Services;

namespace FortniteAFKXPMonitor;

public partial class MainWindow : Window
{
    private static readonly IBrush Primary = Brush.Parse("#F1F2FA");
    private static readonly IBrush Good = Brush.Parse("#4ADE80");
    private static readonly IBrush Warn = Brush.Parse("#FBBF24");
    private static readonly IBrush Bad = Brush.Parse("#F87171");
    private static readonly IBrush Accent = Brush.Parse("#7C5CFF");

    private readonly PlatformServices _platform = PlatformServices.Create();
    private readonly AfkEngine _engine;
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly AppSettings _settings = AppSettings.Load();

    // Linux only: what's still missing (input permissions, focus tool), re-read from the real system.
    private readonly DispatcherTimer _setupTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IReadOnlyList<SetupItem> _setup = Array.Empty<SetupItem>();
    private bool _setupChecking;
    private bool _setupActionRunning;

    // The global hotkey and a desktop shortcut running "--toggle" can both fire for one physical key press
    // (see CommandChannel). Toggles arriving this close together count as one.
    private const long ShortcutDebounceMs = 800;
    private long _lastShortcutTick = -ShortcutDebounceMs;
    private readonly CommandChannel.Server _commandServer;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new AfkEngine(_platform.Input, _platform.WindowDetector, () => _platform.Hotkey.AltHeld);
        ApplySettingsToUi();

        ActionMouse.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ActionKey.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ModeAuto.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ModeHold.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        KeyBox.KeyDown += KeyBox_KeyDown;
        HotkeyBox.KeyDown += HotkeyBox_KeyDown;

        // A text box keeps keyboard focus until something else takes it, and clicking empty space doesn't.
        // The key/hotkey boxes also swallow every key (Tab included), so without this you can't leave them.
        // Any click (or tap) that isn't on a text box hands focus to the window itself.
        Focusable = true;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is null)
                Focus();
        }, RoutingStrategies.Tunnel);

        _platform.Mouse.UserMoved += _engine.NotifyManualCameraInput;
        _platform.Hotkey.Pressed += () => Dispatcher.UIThread.Post(ToggleFromShortcut);
        _platform.Hotkey.Register(_settings.Hotkey);
        _commandServer = new CommandChannel.Server(HandleCommandAsync);

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        ShortcutButton.Content = AppShortcut.ButtonText;
        // Quietly look for a newer release (at most every few hours), unless turned off.
        if (_settings.CheckForUpdates && DateTime.UtcNow - _settings.LastUpdateCheckUtc > TimeSpan.FromHours(6))
            _ = CheckForUpdatesAsync(manual: false);

        _ui.Tick += (_, _) => RefreshStatus();
        _ui.Start();
        RefreshStatus();

        if (OperatingSystem.IsLinux())
        {
            RequireTargetBox.IsCheckedChanged += (_, _) => _ = RefreshSetupAsync();
            _setupTimer.Tick += async (_, _) => await RefreshSetupAsync();
            _setupTimer.Start();
            _ = RefreshSetupAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _ui.Stop();
        _setupTimer.Stop();
        _commandServer.Dispose();
        _engine.Stop();
        _platform.Dispose();
        ReadSettingsFromUi();
        _settings.Save();
        base.OnClosed(e);
    }

    // ---- settings <-> UI --------------------------------------------------

    private void ApplySettingsToUi()
    {
        (_settings.Action == ActionKind.Key ? ActionKey : ActionMouse).IsChecked = true;
        (_settings.Mode == RepeatMode.Hold ? ModeHold : ModeAuto).IsChecked = true;
        (_settings.MouseButton switch
        {
            MouseButtonKind.Right => BtnRight,
            MouseButtonKind.Middle => BtnMiddle,
            _ => BtnLeft,
        }).IsChecked = true;

        KeyBox.Text = _settings.Key.ToString();
        HotkeyBox.Text = _settings.Hotkey.ToString();
        IntervalBox.Text = _settings.IntervalMs.ToString();
        NudgeMinutesBox.Text = _settings.NudgeMinutes.ToString("0.##");
        NudgePixelsBox.Text = _settings.NudgePixels.ToString();
        RequireTargetBox.IsChecked = _settings.RequireTarget;
        AutoUpdateBox.IsChecked = _settings.CheckForUpdates;
        UpdateRowVisibility();
    }

    private void ReadSettingsFromUi()
    {
        _settings.Action = ActionKey.IsChecked == true ? ActionKind.Key : ActionKind.Mouse;
        _settings.Mode = ModeHold.IsChecked == true ? RepeatMode.Hold : RepeatMode.Auto;
        _settings.MouseButton = BtnRight.IsChecked == true ? MouseButtonKind.Right
            : BtnMiddle.IsChecked == true ? MouseButtonKind.Middle
            : MouseButtonKind.Left;

        if (int.TryParse(IntervalBox.Text, out int interval))
            _settings.IntervalMs = Math.Clamp(interval, 10, 60_000);
        if (double.TryParse(NudgeMinutesBox.Text, out double minutes))
            _settings.NudgeMinutes = Math.Clamp(minutes, 0.25, 6.5);
        if (int.TryParse(NudgePixelsBox.Text, out int pixels))
            _settings.NudgePixels = Math.Clamp(pixels, 1, 1000);

        _settings.RequireTarget = RequireTargetBox.IsChecked == true;
        _settings.CheckForUpdates = AutoUpdateBox.IsChecked == true;

        // Echo back the clamped values.
        IntervalBox.Text = _settings.IntervalMs.ToString();
        NudgeMinutesBox.Text = _settings.NudgeMinutes.ToString("0.##");
        NudgePixelsBox.Text = _settings.NudgePixels.ToString();
    }

    private void UpdateRowVisibility()
    {
        if (MouseRow is null) return; // can fire before the controls exist
        bool key = ActionKey.IsChecked == true;
        MouseRow.IsVisible = !key;
        KeyRow.IsVisible = key;
        IntervalRow.IsVisible = ModeHold.IsChecked != true;
    }

    private void KeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (!KeyMap.IsSupported(e.Key)) return;
        _settings.Key = e.Key;
        KeyBox.Text = e.Key.ToString();
    }

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (!KeyMap.IsSupported(e.Key)) return;

        var previous = _settings.Hotkey;
        _settings.Hotkey = e.Key;
        HotkeyBox.Text = e.Key.ToString();
        if (!_platform.Hotkey.Register(e.Key))
        {
            _settings.Hotkey = previous;
            HotkeyBox.Text = previous.ToString();
            _platform.Hotkey.Register(previous);
        }
    }

    // ---- start / stop -----------------------------------------------------

    private void Toggle_Click(object? sender, RoutedEventArgs e) => Toggle();

    private void ToggleFromShortcut()
    {
        long now = Environment.TickCount64;
        if (now - _lastShortcutTick < ShortcutDebounceMs)
            return;
        _lastShortcutTick = now;
        Toggle();
    }

    /// <summary>Runs a --toggle / --start / --stop / --status command from another process on the UI thread.</summary>
    private Task<string> HandleCommandAsync(string command) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            switch (command)
            {
                case "toggle": ToggleFromShortcut(); break;
                case "start" when !_engine.IsRunning: Toggle(); break;
                case "stop" when _engine.IsRunning: Toggle(); break;
            }
            return _engine.IsRunning ? "running" : "stopped";
        }).GetTask();

    private void Toggle()
    {
        if (_engine.IsRunning)
        {
            _platform.Mouse.Stop();
            _engine.Stop();
        }
        else
        {
            if (_platform.Input.Problem is not null)
            {
                RefreshStatus();
                return;
            }

            ReadSettingsFromUi();
            _settings.Save();
            _engine.Start(_settings);
            _platform.Mouse.Start();
        }
        RefreshStatus();
    }

    // ---- status -----------------------------------------------------------

    private void RefreshStatus()
    {
        bool running = _engine.IsRunning;
        SettingsPanel.IsEnabled = !running;
        NudgePanel.IsEnabled = !running;
        ToggleButton.Content = running ? "Stop" : "Start";
        string? shortcutNote = _platform.Hotkey.GlobalShortcutStatus;
        HotkeyNote.IsVisible = !string.IsNullOrEmpty(shortcutNote);
        HotkeyNote.Text = shortcutNote ?? "";
        if (running) ToggleButton.Classes.Add("stop"); else ToggleButton.Classes.Remove("stop");

        bool paused = _engine.IsPaused;
        string hotkey = _settings.Hotkey.ToString();

        if (!running && !paused)
        {
            SetStatus("Stopped", Primary);
            TargetText.Text = $"Press {hotkey} or Start, then switch to your game.";
            NudgeCountdown.Text = "--:--";
            SinceNudge.Text = "--:--";
            TimeoutBar.Value = 0;
            StatsText.Text = "7:00 XP timeout window";
            return;
        }

        if (paused)
        {
            SetStatus("Paused", Warn);
            TargetText.Text = $"Countdown frozen. Press {hotkey} or Start to resume.";
        }
        else if (_engine.AltPaused)
        {
            SetStatus("Paused - Alt held", Warn);
            TargetText.Text = "Input is on hold while you switch windows (countdown keeps running).";
        }
        else
        {
            var target = _engine.Target;
            if (!_settings.RequireTarget || target.IsTarget)
            {
                SetStatus("Running", Good);
                TargetText.Text = target.IsTarget ? $"Active in: {target.Label}" : $"Focused: {target.Label} (focus check off)";
            }
            else
            {
                SetStatus("Auto-paused - waiting for game", Warn);
                TargetText.Text = $"Focused: {target.Label}. Input resumes when you return to the game (countdown keeps running).";
            }
        }

        double until = _engine.SecondsUntilNudge;
        double since = _engine.SecondsSinceNudge;
        NudgeCountdown.Text = until <= 0 ? "due" : FormatTime(until);
        SinceNudge.Text = FormatTime(since);
        TimeoutBar.Value = Math.Min(since, AfkEngine.XpTimeoutSeconds);
        TimeoutBar.Foreground = since > AfkEngine.XpTimeoutSeconds - 60 ? Bad
            : since > AfkEngine.XpTimeoutSeconds - 150 ? Warn
            : Accent;
        StatsText.Text = $"{_engine.ActionCount:N0} inputs sent · {_engine.NudgeCount} camera nudges · {_engine.ManualResetCount} manual resets";
    }

    // ---- shortcut and updates ---------------------------------------------

    private UpdateInfo? _update;
    private bool _updating;

    private void ShowAppMessage(string text, bool? good)
    {
        AppMessage.Text = text;
        AppMessage.Foreground = good switch { true => Good, false => Warn, _ => Primary };
        AppMessage.IsVisible = true;
    }

    private void Shortcut_Click(object? sender, RoutedEventArgs e)
    {
        ShortcutResult result = AppShortcut.Install();
        ShowAppMessage(result.Message, result.Success);
    }

    private async void UpdateCheck_Click(object? sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <param name="manual">True when the user asked: say so when there's nothing new, or when it fails.</param>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        UpdateCheckButton.IsEnabled = false;
        if (manual) ShowAppMessage("Checking for updates...", null);

        try
        {
            UpdateCheck check = await UpdateService.CheckAsync();
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;

            if (check.Update is { } info)
            {
                _update = info;
                UpdateText.Text = $"{info.Tag} is out (you have v{UpdateService.CurrentVersion}).";
                UpdateCard.IsVisible = true;
                if (manual) ShowAppMessage($"{info.Tag} is available - see the green card at the top.", true);
            }
            else if (manual)
            {
                ShowAppMessage($"You're up to date (v{UpdateService.CurrentVersion}).", true);
            }
        }
        catch (Exception ex)
        {
            if (manual) ShowAppMessage($"Couldn't check for updates: {ex.Message}", false);   // a failed startup check stays silent
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private void ReleaseNotes_Click(object? sender, RoutedEventArgs e) =>
        UpdateService.OpenInBrowser(_update?.ReleaseUrl ?? UpdateService.ReleasesPage);

    private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        if (_update is not { } info || _updating) return;
        _updating = true;

        // Nothing should be pressed or clicking while the app swaps itself out.
        if (_engine.IsRunning)
        {
            _platform.Mouse.Stop();
            _engine.Stop();
            RefreshStatus();
        }

        UpdateNowButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.IsVisible = true;
        var progress = new Progress<double>(p =>
        {
            UpdateProgress.Value = p * 100;
            UpdateText.Text = $"Downloading {info.Tag}... {p:P0}";
        });

        try
        {
            string installed = await UpdateService.InstallAsync(info, progress);
            UpdateText.Text = "Updated. Restarting...";
            ReadSettingsFromUi();
            _settings.Save();
            UpdateService.RestartApp(installed);   // waits for this process to exit before it starts up
            Close();
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Couldn't update: {ex.Message}";
            UpdateProgress.IsVisible = false;
            UpdateNowButton.IsEnabled = true;
            _updating = false;
            UpdateCheckButton.IsEnabled = true;
        }
    }

    // ---- Linux setup card -------------------------------------------------

    /// <summary>
    /// Re-checks what's missing and updates the card. Polls while something is missing and stops once
    /// everything is fine, so on a configured system it costs one check at launch and then nothing.
    /// </summary>
    private async Task RefreshSetupAsync()
    {
        if (_setupChecking) return;
        _setupChecking = true;
        try
        {
            bool focusCheck = RequireTargetBox.IsChecked == true;
            _setup = await Task.Run(() => LinuxSetup.Check(_platform.WindowDetector, focusCheck));
            UpdateSetupCard();

            if (_setup.All(i => i.State == SetupState.Ok))
                _setupTimer.Stop();
            else if (!_setupTimer.IsEnabled)
                _setupTimer.Start();
        }
        finally
        {
            _setupChecking = false;
        }
    }

    private void UpdateSetupCard()
    {
        SetupItem? input = _setup.FirstOrDefault(i => i.Id == LinuxSetup.InputId && i.State != SetupState.Ok);
        SetupItem? tool = _setup.FirstOrDefault(i => i.Id == LinuxSetup.ToolId && i.State != SetupState.Ok);

        InputRow.IsVisible = input is not null;
        InputText.Text = input?.Detail ?? "";
        InputButton.IsVisible = input?.CanFix == true;
        InputButton.IsEnabled = !_setupActionRunning;

        ToolRow.IsVisible = tool is not null;
        ToolText.Text = tool?.Detail ?? "";
        ToolButton.IsVisible = tool?.CanFix == true;
        ToolButton.IsEnabled = !_setupActionRunning;

        bool anyMissing = input is not null || tool is not null;
        SetupCard.IsVisible = anyMissing;
        if (!anyMissing)
            SetupMessage.IsVisible = false;
    }

    private void ShowSetupMessage(SetupResult result)
    {
        SetupMessage.Text = result.Message;
        SetupMessage.Foreground = result.Success ? Good : Warn;
        SetupMessage.IsVisible = true;
    }

    private async void InputFix_Click(object? sender, RoutedEventArgs e)
    {
        _setupActionRunning = true;
        UpdateSetupCard();
        SetupMessage.Text = "Waiting for the password prompt...";
        SetupMessage.Foreground = Primary;
        SetupMessage.IsVisible = true;

        SetupResult result = await LinuxSetup.FixInputAccessAsync();

        _setupActionRunning = false;
        ShowSetupMessage(result);
        await RefreshSetupAsync();
    }

    private async void ToolFix_Click(object? sender, RoutedEventArgs e)
    {
        string? tool = _platform.WindowDetector.RequiredTool;
        if (tool is null) return;

        ShowSetupMessage(LinuxSetup.StartFocusToolInstall(tool));
        await RefreshSetupAsync();
    }

    private void SetStatus(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
