using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FortniteAFKXPMonitor.Services;

namespace FortniteAFKXPMonitor;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xAF01;

    private readonly AfkEngine _engine = new();
    private readonly MouseActivityMonitor _mouse = new();
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private AppSettings _settings = AppSettings.Load();
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        ApplySettingsToUi();

        ActionMouse.Checked += (_, _) => UpdateRowVisibility();
        ActionKey.Checked += (_, _) => UpdateRowVisibility();
        ModeAuto.Checked += (_, _) => UpdateRowVisibility();
        ModeHold.Checked += (_, _) => UpdateRowVisibility();

        _mouse.UserMoved += _engine.NotifyManualCameraInput;

        _ui.Tick += (_, _) => RefreshStatus();
        _ui.Start();
        RefreshStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkey();
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Stop();
        _mouse.Dispose();
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        ReadSettingsFromUi();
        _settings.Save();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RegisterHotkey()
    {
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        bool ok = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, NativeMethods.MOD_NOREPEAT, (uint)_settings.HotkeyVk);
        HotkeyNote.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        HotkeyNote.Text = ok ? "" : "That hotkey is already in use by another app. Pick a different one.";
    }

    // ---- settings <-> UI --------------------------------------------------

    private void ApplySettingsToUi()
    {
        (_settings.Action == ActionKind.Key ? ActionKey : ActionMouse).IsChecked = true;
        (_settings.Mode == ClickMode.Hold ? ModeHold : ModeAuto).IsChecked = true;
        (_settings.MouseButton switch
        {
            MouseButtonKind.Right => BtnRight,
            MouseButtonKind.Middle => BtnMiddle,
            _ => BtnLeft,
        }).IsChecked = true;

        KeyBox.Text = KeyName(_settings.KeyVk);
        HotkeyBox.Text = KeyName(_settings.HotkeyVk);
        IntervalBox.Text = _settings.IntervalMs.ToString();
        NudgeMinutesBox.Text = _settings.NudgeMinutes.ToString("0.##");
        NudgePixelsBox.Text = _settings.NudgePixels.ToString();
        RequireTargetBox.IsChecked = _settings.RequireTarget;
        UpdateRowVisibility();
    }

    private void ReadSettingsFromUi()
    {
        _settings.Action = ActionKey.IsChecked == true ? ActionKind.Key : ActionKind.Mouse;
        _settings.Mode = ModeHold.IsChecked == true ? ClickMode.Hold : ClickMode.Auto;
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
        if (MouseRow is null) return; // fires during InitializeComponent
        bool key = ActionKey.IsChecked == true;
        MouseRow.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        KeyRow.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        IntervalRow.Visibility = ModeHold.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string KeyName(int vk) => KeyInterop.KeyFromVirtualKey(vk).ToString();

    private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk == 0) return;
        _settings.KeyVk = vk;
        KeyBox.Text = KeyName(vk);
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk == 0) return;
        _settings.HotkeyVk = vk;
        HotkeyBox.Text = KeyName(vk);
        RegisterHotkey();
    }

    // ---- start / stop -----------------------------------------------------

    private void Toggle_Click(object sender, RoutedEventArgs e) => Toggle();

    private void Toggle()
    {
        if (_engine.IsRunning)
        {
            _mouse.Stop();
            _engine.Stop();
        }
        else
        {
            ReadSettingsFromUi();
            _settings.Save();
            _engine.Start(_settings);
            _mouse.Start();
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
        ToggleButton.Background = running ? (Brush)FindResource("Bad") : (Brush)FindResource("Accent");

        bool paused = _engine.IsPaused;

        if (!running && !paused)
        {
            SetStatus("Stopped", "TextPrimary");
            TargetText.Text = $"Press {KeyName(_settings.HotkeyVk)} or Start, then switch to your game.";
            NudgeCountdown.Text = "--:--";
            SinceNudge.Text = "--:--";
            TimeoutBar.Value = 0;
            StatsText.Text = "7:00 XP timeout window";
            return;
        }

        if (paused)
        {
            SetStatus("Paused", "Warn");
            TargetText.Text = $"Countdown frozen. Press {KeyName(_settings.HotkeyVk)} or Start to resume.";
        }
        else
        {
            var target = _engine.Target;
            if (!_settings.RequireTarget || target.IsTarget)
            {
                SetStatus("Running", "Good");
                TargetText.Text = target.IsTarget ? $"Active in: {target.Label}" : $"Focused: {target.Label} (focus check off)";
            }
            else
            {
                SetStatus("Auto-paused - waiting for game", "Warn");
                TargetText.Text = $"Focused: {target.Label}. Input resumes when you return to the game (countdown keeps running).";
            }
        }

        double until = _engine.SecondsUntilNudge;
        double since = _engine.SecondsSinceNudge;
        NudgeCountdown.Text = until <= 0 ? "due" : FormatTime(until);
        SinceNudge.Text = FormatTime(since);
        TimeoutBar.Value = Math.Min(since, AfkEngine.XpTimeoutSeconds);
        TimeoutBar.Foreground = (Brush)FindResource(
            since > AfkEngine.XpTimeoutSeconds - 60 ? "Bad" : since > AfkEngine.XpTimeoutSeconds - 150 ? "Warn" : "Accent");
        StatsText.Text = $"{_engine.ActionCount:N0} inputs sent · {_engine.NudgeCount} camera nudges · {_engine.ManualResetCount} manual resets";
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(brushKey);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
