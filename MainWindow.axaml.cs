using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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

    public MainWindow()
    {
        InitializeComponent();
        _engine = new AfkEngine(_platform.Input, _platform.WindowDetector);
        ApplySettingsToUi();

        ActionMouse.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ActionKey.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ModeAuto.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        ModeHold.IsCheckedChanged += (_, _) => UpdateRowVisibility();
        KeyBox.KeyDown += KeyBox_KeyDown;
        HotkeyBox.KeyDown += HotkeyBox_KeyDown;

        _platform.Mouse.UserMoved += _engine.NotifyManualCameraInput;
        _platform.Hotkey.Pressed += () => Dispatcher.UIThread.Post(Toggle);
        _platform.Hotkey.Register(_settings.Hotkey);

        _ui.Tick += (_, _) => RefreshStatus();
        _ui.Start();
        RefreshStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _ui.Stop();
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
        if (running) ToggleButton.Classes.Add("stop"); else ToggleButton.Classes.Remove("stop");

        UpdateWarnings();

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

    /// <summary>Shows anything the platform layer couldn't set up (permissions, unsupported desktop...).</summary>
    private void UpdateWarnings()
    {
        var problems = new List<string>();
        if (_platform.Input.Problem is { } a) problems.Add(a);
        if (_platform.Hotkey.Problem is { } b) problems.Add(b);
        if (_platform.Mouse.Problem is { } c) problems.Add(c);
        if (_platform.WindowDetector.Problem is { } d) problems.Add(d);

        WarningText.IsVisible = problems.Count > 0;
        WarningText.Text = string.Join("\n", problems);
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
