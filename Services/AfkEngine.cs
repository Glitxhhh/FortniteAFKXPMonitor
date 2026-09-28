using System.Diagnostics;
using static FortniteAFKXPMonitor.Services.NativeMethods;

namespace FortniteAFKXPMonitor.Services;

/// <summary>
/// Runs the clicker / key-holder and the periodic camera nudge on a background thread.
/// The countdown is driven purely by the running state, not by which window is focused;
/// focus only gates whether input is actually sent.
/// </summary>
public sealed class AfkEngine
{
    /// <summary>Fortnite stops awarding AFK XP after this long without camera input.</summary>
    public const double XpTimeoutSeconds = 7 * 60;

    private readonly Random _rng = Random.Shared;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Snapshot values read by the UI thread.
    private long _nudgeDueTicks;
    private long _lastNudgeTicks;
    private volatile TargetInfo _target = new(false, "None", "");
    private long _actions;
    private int _nudges;

    // Set while the user has toggled the clicker off; timers are frozen at this instant.
    private long _pausedAtTicks;
    private bool _hasSession;

    private AppSettings _settings = new();
    private long _lastManualTicks;
    private int _manualResets;

    public bool IsRunning => _cts is not null;

    /// <summary>How many times a real mouse movement reset the countdown this session.</summary>
    public int ManualResetCount => Volatile.Read(ref _manualResets);

    public int NudgeCount => Volatile.Read(ref _nudges);
    public long ActionCount => Interlocked.Read(ref _actions);
    public TargetInfo Target => _target;

    /// <summary>True once started and toggled off again; the countdown is frozen until the next Start.</summary>
    public bool IsPaused => !IsRunning && _hasSession;

    // While paused, time stands still at the moment of the toggle.
    private long ClockNow => IsRunning || !_hasSession ? Stopwatch.GetTimestamp() : Interlocked.Read(ref _pausedAtTicks);

    public double SecondsUntilNudge =>
        Math.Max(0, (Interlocked.Read(ref _nudgeDueTicks) - ClockNow) / (double)Stopwatch.Frequency);

    public double SecondsSinceNudge =>
        (ClockNow - Interlocked.Read(ref _lastNudgeTicks)) / (double)Stopwatch.Frequency;

    public void Start(AppSettings settings)
    {
        if (IsRunning)
            return;

        var s = settings.Clone();
        _settings = s;
        var now = Stopwatch.GetTimestamp();
        if (_hasSession)
        {
            // Resume: push both marks forward by however long we were paused.
            long paused = now - Interlocked.Read(ref _pausedAtTicks);
            Interlocked.Add(ref _lastNudgeTicks, paused);
            Interlocked.Add(ref _nudgeDueTicks, paused);
        }
        else
        {
            StartFreshSession(s, now);
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(s, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts is null)
            return;

        cts.Cancel();
        try { _loop?.Wait(1000); } catch { /* cancelled */ }
        cts.Dispose();
        _cts = null;
        _loop = null;
        Interlocked.Exchange(ref _pausedAtTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Call when a real mouse moved. If a target window is focused, the game just saw camera input,
    /// so the XP timeout restarts and the next nudge is pushed back a full interval.
    /// </summary>
    public void NotifyManualCameraInput()
    {
        if (!IsRunning) return;
        if (_settings.RequireTarget && !_target.IsTarget) return;

        long now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastManualTicks) < Stopwatch.Frequency / 4) return; // throttle
        Interlocked.Exchange(ref _lastManualTicks, now);

        Interlocked.Exchange(ref _lastNudgeTicks, now);
        Interlocked.Exchange(ref _nudgeDueTicks, now + NextNudgeDelayTicks(_settings));
        Interlocked.Increment(ref _manualResets);
    }

    private void StartFreshSession(AppSettings s, long now)
    {
        Interlocked.Exchange(ref _lastNudgeTicks, now);
        Interlocked.Exchange(ref _nudgeDueTicks, now + NextNudgeDelayTicks(s));
        Interlocked.Exchange(ref _actions, 0);
        Volatile.Write(ref _nudges, 0);
        Volatile.Write(ref _manualResets, 0);
        _hasSession = true;
    }

    private void Run(AppSettings s, CancellationToken ct)
    {
        timeBeginPeriod(1);
        bool held = false;
        bool wasBlocked = false;
        long nextAction = 0;
        long intervalTicks = Math.Max(10, s.IntervalMs) * Stopwatch.Frequency / 1000;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _target = TargetDetector.GetForeground();
                bool canSend = !s.RequireTarget || _target.IsTarget;

                if (!canSend)
                {
                    // Never leave a button/key stuck down in another app.
                    if (held)
                    {
                        Release(s);
                        held = false;
                    }
                    wasBlocked = true;
                }
                else
                {
                    // Give the streaming client a moment to recapture input after focus returns.
                    if (wasBlocked)
                    {
                        wasBlocked = false;
                        if (ct.WaitHandle.WaitOne(150)) break;
                    }

                    long now = Stopwatch.GetTimestamp();

                    if (s.Mode == ClickMode.Hold)
                    {
                        if (!held)
                        {
                            // The release sent on focus loss goes to whatever window was focused, so the game
                            // may still think the input is down. Send a fresh up -> down so it registers.
                            Release(s);
                            if (ct.WaitHandle.WaitOne(50)) break;
                            Press(s);
                            held = true;
                            Interlocked.Increment(ref _actions);
                        }
                    }
                    else if (now >= nextAction)
                    {
                        Tap(s, ct);
                        Interlocked.Increment(ref _actions);
                        nextAction = Stopwatch.GetTimestamp() + intervalTicks;
                    }

                    if (now >= Interlocked.Read(ref _nudgeDueTicks))
                    {
                        NudgeCamera(s, ct);
                        long done = Stopwatch.GetTimestamp();
                        Interlocked.Exchange(ref _lastNudgeTicks, done);
                        Interlocked.Exchange(ref _nudgeDueTicks, done + NextNudgeDelayTicks(s));
                        Interlocked.Increment(ref _nudges);
                    }
                }

                ct.WaitHandle.WaitOne(2);
            }
        }
        finally
        {
            if (held)
                Release(s);
            timeEndPeriod(1);
        }
    }

    private static void Press(AppSettings s)
    {
        if (s.Action == ActionKind.Mouse) InputSimulator.MouseDown(s.MouseButton);
        else InputSimulator.KeyDown(s.KeyVk);
    }

    private static void Release(AppSettings s)
    {
        if (s.Action == ActionKind.Mouse) InputSimulator.MouseUp(s.MouseButton);
        else InputSimulator.KeyUp(s.KeyVk);
    }

    private static void Tap(AppSettings s, CancellationToken ct)
    {
        Press(s);
        ct.WaitHandle.WaitOne(8);
        Release(s);
    }

    /// <summary>
    /// Moves the camera out and back so the net change is zero, but never the same way twice:
    /// random axis/direction, distance, speed, curved path, dwell time, and return route all vary.
    /// </summary>
    private void NudgeCamera(AppSettings s, CancellationToken ct)
    {
        double distance = Math.Max(1, s.NudgePixels) * (0.6 + _rng.NextDouble() * 0.8);
        double angle = _rng.Next(3) switch
        {
            0 => 0,                                   // horizontal
            1 => Math.PI / 2,                         // vertical
            _ => _rng.NextDouble() * Math.PI / 2,     // diagonal
        };
        if (_rng.Next(2) == 0) angle += Math.PI;      // flip direction

        // Vertical movement is kept smaller than horizontal so the view doesn't tilt far.
        double vScale = 0.5;
        int dx = (int)Math.Round(Math.Cos(angle) * distance);
        int dy = (int)Math.Round(Math.Sin(angle) * distance * vScale);
        if (dx == 0 && dy == 0) dx = 1;

        if (!MoveSmooth(dx, dy, ct)) return;

        // Occasionally linger, sometimes just a beat, like a person looking around.
        if (ct.WaitHandle.WaitOne(_rng.Next(60, 450))) return;

        // Return the exact same distance, on a different curve and speed.
        MoveSmooth(-dx, -dy, ct);
    }

    /// <summary>Eased, slightly curved mouse move that always sums to exactly (dx, dy). False if cancelled.</summary>
    private bool MoveSmooth(int dx, int dy, CancellationToken ct)
    {
        int steps = _rng.Next(8, 20);
        double arc = (_rng.NextDouble() - 0.5) * 0.35 * Math.Sqrt(dx * (double)dx + dy * (double)dy);
        double px = -dy, py = dx;                       // perpendicular
        double len = Math.Sqrt(px * px + py * py);
        if (len > 0) { px /= len; py /= len; }

        int prevX = 0, prevY = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double ease = t * t * (3 - 2 * t);          // smoothstep
            double bow = Math.Sin(Math.PI * t) * arc;
            int x = (int)Math.Round(dx * ease + px * bow);
            int y = (int)Math.Round(dy * ease + py * bow);
            if (i == steps) { x = dx; y = dy; }         // land exactly

            if (x != prevX || y != prevY)
                InputSimulator.MouseMoveRelative(x - prevX, y - prevY);
            prevX = x; prevY = y;

            if (ct.WaitHandle.WaitOne(_rng.Next(7, 22))) return false;
        }
        return true;
    }

    /// <summary>Configured interval with +/-10% jitter, capped safely under the 7 minute XP timeout.</summary>
    private long NextNudgeDelayTicks(AppSettings s)
    {
        double seconds = s.NudgeMinutes * 60 * (0.9 + _rng.NextDouble() * 0.2);
        seconds = Math.Clamp(seconds, 15, XpTimeoutSeconds - 30);
        return (long)(seconds * Stopwatch.Frequency);
    }
}
