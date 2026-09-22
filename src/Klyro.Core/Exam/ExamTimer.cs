using System;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Exam;

public delegate void ExamTimerTickEventHandler(TimeSpan remainingTime, TimeSpan elapsedTime);

/// <summary>
/// Native C# examination timer independent of WebView / JavaScript execution.
/// </summary>
public sealed class ExamTimer : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _totalDuration;
    private readonly string _timerPolicy;
    private TimeSpan _remainingTime;
    private TimeSpan _elapsedTime;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;
    private bool _isDisposed;

    public TimeSpan TotalDuration => _totalDuration;
    public TimeSpan RemainingTime => _remainingTime;
    public TimeSpan ElapsedTime => _elapsedTime;
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public string TimerPolicy => _timerPolicy;

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
                return;
            }

            IsRunning = true;
            IsPaused = false;
            _timerCts = new CancellationTokenSource();
            _timerTask = Task.Run(() => RunTimerLoopAsync(_timerCts.Token));
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsRunning || IsPaused) return;

            IsPaused = true;
            Paused?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!IsRunning || !IsPaused) return;

            IsPaused = false;
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsRunning = false;
            IsPaused = false;
            _timerCts?.Cancel();
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
        // If policy is "ContinueDuringNetworkFailure", timer intentionally keeps running
    }

    private async Task RunTimerLoopAsync(CancellationToken cancellationToken)
    {
        using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!cancellationToken.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(cancellationToken))
        {
            bool hasExpired = false;
            TimeSpan currentRemaining;
            TimeSpan currentElapsed;

            lock (_lock)
            {
                if (!IsRunning) break;
                if (IsPaused) continue;

                if (_remainingTime > TimeSpan.FromSeconds(1))
                {
                    _remainingTime -= TimeSpan.FromSeconds(1);
                    _elapsedTime += TimeSpan.FromSeconds(1);
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
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();
        _timerCts?.Dispose();
    }
}
