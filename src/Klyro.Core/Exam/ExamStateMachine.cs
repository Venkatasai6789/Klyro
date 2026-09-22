using System;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Persistence;

namespace Klyro.Core.Exam;

public delegate void ExamStateChangedEventHandler(ExamState previousState, ExamState newState);

/// <summary>
/// State machine governing the examination lifecycle and coordinating timer, persistence, and recovery.
/// </summary>
public sealed class ExamStateMachine
{
    private readonly object _lock = new();
    private readonly KlyroConfiguration _config;
    private readonly ISessionStore _sessionStore;
    private readonly IAuditLogger? _auditLogger;

    public ExamState CurrentState { get; private set; } = ExamState.INITIALIZING;
    public ExamState PreviousState { get; private set; } = ExamState.INITIALIZING;
    public ExamTimer Timer { get; }
    public SessionRecord CurrentSession { get; }

    public event ExamStateChangedEventHandler? StateChanged;

    public ExamStateMachine(
        KlyroConfiguration config,
        ISessionStore sessionStore,
        IAuditLogger? auditLogger = null,
        SessionRecord? existingSession = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _auditLogger = auditLogger;

        CurrentSession = existingSession ?? new SessionRecord
        {
            ExamId = _config.Exam.ContestId ?? Guid.NewGuid().ToString(),
            LastKnownAssessmentUrl = _config.Exam.StartUrl,
            RemainingSeconds = TimeSpan.FromMinutes(_config.Exam.DurationMinutes).TotalSeconds
        };

        var duration = TimeSpan.FromSeconds(CurrentSession.RemainingSeconds > 0
            ? CurrentSession.RemainingSeconds
            : TimeSpan.FromMinutes(_config.Exam.DurationMinutes).TotalSeconds);

        Timer = new ExamTimer(duration, _config.Exam.TimerPolicy);
        Timer.Tick += OnTimerTick;
        Timer.TimeExpired += OnTimerExpired;
    }

    public void TransitionTo(ExamState newState)
    {
        lock (_lock)
        {
            if (CurrentState == newState) return;

            var oldState = CurrentState;
            PreviousState = oldState;
            CurrentState = newState;

            CurrentSession.CurrentExamState = newState;

            // Handle timer based on state transitions
            if (newState == ExamState.ACTIVE)
            {
                Timer.Start();
            }
            else if (newState == ExamState.NETWORK_RECOVERY || newState == ExamState.PAUSED)
            {
                Timer.OnNetworkRecoveryStateChanged(isRecovering: true);
            }
            else if (newState == ExamState.RESUMING)
            {
                Timer.OnNetworkRecoveryStateChanged(isRecovering: false);
            }
            else if (newState == ExamState.COMPLETED || newState == ExamState.FAILED)
            {
                Timer.Stop();
                CurrentSession.IsCompleted = true;
            }

            // Save recovery record to disk
            _sessionStore.SaveSession(CurrentSession);

            LogTransition(oldState, newState);
            StateChanged?.Invoke(oldState, newState);
        }
    }

    private void OnTimerTick(TimeSpan remaining, TimeSpan elapsed)
    {
        CurrentSession.RemainingSeconds = remaining.TotalSeconds;
        // Periodic background save of remaining time
        if (((int)elapsed.TotalSeconds) % 15 == 0)
        {
            _sessionStore.SaveSession(CurrentSession);
        }
    }

    private void OnTimerExpired(object? sender, EventArgs e)
    {
        TransitionTo(ExamState.COMPLETED);
    }

    private void LogTransition(ExamState oldState, ExamState newState)
    {
        var eventType = newState switch
        {
            ExamState.ACTIVE => EventType.EXAM_STARTED,
            ExamState.COMPLETED => EventType.EXAM_COMPLETED,
            ExamState.PAUSED => EventType.EXAM_PAUSED,
            ExamState.RESUMING => EventType.EXAM_RESUMED,
            ExamState.NETWORK_RECOVERY => EventType.RECOVERY_UI_SHOWN,
            _ => EventType.EXAM_STARTED
        };

        _auditLogger?.Log(eventType, newState.ToString(), $"Exam transitioned from {oldState} to {newState}", CurrentSession.LastKnownAssessmentUrl, CurrentSession.ExamId);
    }
}
