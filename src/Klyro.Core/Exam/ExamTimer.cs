using System;
using System.Diagnostics;
using System.Threading;

namespace Klyro.Core.Exam;

public delegate void ExamTimerTickEventHandler(TimeSpan remainingTime, TimeSpan elapsedTime);

/// <summary>
/// Hardware-precision monotonic examination timer executing on a dedicated OS thread ("Klyro_ExamTimer_Engine").
/// Utilizes QueryPerformanceCounter (Stopwatch.GetTimestamp) to guarantee tamper-proof, non-drifting
/// time measurement immune to OS clock modifications, sleep states, or ThreadPool delays.
/// </summary>
public sealed class ExamTimer : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _totalDuration;
    private readonly string _timerPolicy;
    private TimeSpan _remainingTime;
    private TimeSpan _elapsedTime;
    private CancellationTokenSource? _timerCts;
    private readonly ManualResetEventSlim _wakeUpEvent = new(false);
    private Thread? _timerThread;
    private bool _isDisposed;

    public TimeSpan TotalDuration => _totalDuration;
    public TimeSpan RemainingTime
    {
        get { lock (_lock) return _remainingTime; }
    }
    public TimeSpan ElapsedTime
    {
        get { lock (_lock) return _elapsedTime; }
    }
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public string TimerPolicy => _timerPolicy;
    public Thread? WorkerThread => _timerThread;

    public event ExamTimerTickEventHandler? Tick;
    public event EventHandler? TimeExpired;
    public event EventHandler? Paused;
    public event EventHandler? Resumed;

    public ExamTimer(TimeSpan duration, string timerPolicy = "PauseDuringVerifiedNetworkFailure")
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Exam duration must be greater than zero.");

        _totalDuration = duration;
        _remainingTime = duration;
        _elapsedTime = TimeSpan.Zero;
        _timerPolicy = timerPolicy;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning && !IsPaused) return;

            if (IsPaused)
            {
                IsPaused = false;
                Resumed?.Invoke(this, EventArgs.Empty);
                _wakeUpEvent.Set();
                return;
            }

            IsRunning = true;
            IsPaused = false;
            _timerCts = new CancellationTokenSource();
            _timerThread = new Thread(RunDedicatedTimerLoop)
            {
                Name = "Klyro_ExamTimer_Engine",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _timerThread.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsRunning || IsPaused) return;

            IsPaused = true;
            Paused?.Invoke(this, EventArgs.Empty);
            _wakeUpEvent.Set();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!IsRunning || !IsPaused) return;

            IsPaused = false;
            Resumed?.Invoke(this, EventArgs.Empty);
            _wakeUpEvent.Set();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsRunning = false;
            IsPaused = false;
            _timerCts?.Cancel();
            _wakeUpEvent.Set();
        }
    }

    /// <summary>
    /// Notifies the timer of network recovery events according to the configured policy.
    /// </summary>
    public void OnNetworkRecoveryStateChanged(bool isRecovering)
    {
        if (string.Equals(_timerPolicy, "PauseDuringVerifiedNetworkFailure", StringComparison.OrdinalIgnoreCase))
        {
            if (isRecovering)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }
    }

    private void RunDedicatedTimerLoop()
    {
        var token = _timerCts?.Token ?? CancellationToken.None;
        long lastTimestamp = Stopwatch.GetTimestamp();

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Wait 1 second (or until explicitly woken up on pause/resume)
                _wakeUpEvent.Wait(1000, token);
                _wakeUpEvent.Reset();

                long currentTimestamp = Stopwatch.GetTimestamp();
                double elapsedSeconds = (double)(currentTimestamp - lastTimestamp) / Stopwatch.Frequency;
                lastTimestamp = currentTimestamp;

                bool hasExpired = false;
                TimeSpan currentRemaining;
                TimeSpan currentElapsed;

                lock (_lock)
                {
                    if (!IsRunning) break;
                    if (IsPaused) continue;

                    var delta = TimeSpan.FromSeconds(elapsedSeconds);
                    if (_remainingTime > delta)
                    {
                        _remainingTime -= delta;
                        _elapsedTime += delta;
                    }
                    else
                    {
                        _remainingTime = TimeSpan.Zero;
                        _elapsedTime = _totalDuration;
                        IsRunning = false;
                        hasExpired = true;
                    }

                    currentRemaining = _remainingTime;
                    currentElapsed = _elapsedTime;
                }

                Tick?.Invoke(currentRemaining, currentElapsed);

                if (hasExpired)
                {
                    TimeExpired?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        if (_timerThread != null && _timerThread.IsAlive)
        {
            _timerThread.Join(500);
        }

        _wakeUpEvent.Dispose();
        _timerCts?.Dispose();
    }
}
