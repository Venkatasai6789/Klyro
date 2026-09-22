using System;
using System.Collections.Generic;
using System.IO;
using Klyro.Core.Configuration;
using Klyro.Core.Exam;
using Klyro.Core.Persistence;
using Xunit;

namespace Klyro.UnitTests;

public class ExamStateMachineTests : IDisposable
{
    private readonly string _testDir;
    private readonly SessionStore _store;
    private readonly KlyroConfiguration _config;
    private readonly ExamStateMachine _stateMachine;

    public ExamStateMachineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Klyro_StateTests_" + Guid.NewGuid().ToString("N"));
        _store = new SessionStore(_testDir);
        _config = new KlyroConfiguration
        {
            Exam = new ExamOptions
            {
                ContestId = "TEST_CONTEST",
                StartUrl = "https://www.codechef.com/TEST_CONTEST",
                DurationMinutes = 60,
                TimerPolicy = "PauseDuringVerifiedNetworkFailure"
            }
        };

        _stateMachine = new ExamStateMachine(_config, _store);
    }

    [Fact]
    public void InitialState_IsInitializing()
    {
        Assert.Equal(ExamState.INITIALIZING, _stateMachine.CurrentState);
        Assert.Equal("TEST_CONTEST", _stateMachine.CurrentSession.ExamId);
    }

    [Fact]
    public void TransitionToActive_StartsTimerAndPersistsSession()
    {
        _stateMachine.TransitionTo(ExamState.PREFLIGHT);
        _stateMachine.TransitionTo(ExamState.READY);
        _stateMachine.TransitionTo(ExamState.ACTIVE);

        Assert.Equal(ExamState.ACTIVE, _stateMachine.CurrentState);
        Assert.True(_stateMachine.Timer.IsRunning);

        var saved = _store.LoadActiveSession();
        Assert.NotNull(saved);
        Assert.Equal(ExamState.ACTIVE, saved.CurrentExamState);
    }

    [Fact]
    public void NetworkRecoveryCycle_PausesTimerAndResumes()
    {
        _stateMachine.TransitionTo(ExamState.ACTIVE);
        Assert.False(_stateMachine.Timer.IsPaused);

        // Network goes down -> portal required
        _stateMachine.TransitionTo(ExamState.NETWORK_RECOVERY);
        Assert.Equal(ExamState.NETWORK_RECOVERY, _stateMachine.CurrentState);
        Assert.True(_stateMachine.Timer.IsPaused);

        // Student authenticates -> resuming
        _stateMachine.TransitionTo(ExamState.RESUMING);
        Assert.False(_stateMachine.Timer.IsPaused);

        _stateMachine.TransitionTo(ExamState.ACTIVE);
        Assert.Equal(ExamState.ACTIVE, _stateMachine.CurrentState);
        Assert.False(_stateMachine.Timer.IsPaused);
    }

    [Fact]
    public void StateChangedEvent_FiresOnTransitions()
    {
        var transitions = new List<(ExamState Old, ExamState New)>();
        _stateMachine.StateChanged += (oldState, newState) =>
        {
            transitions.Add((oldState, newState));
        };

        _stateMachine.TransitionTo(ExamState.PREFLIGHT);
        _stateMachine.TransitionTo(ExamState.READY);
        _stateMachine.TransitionTo(ExamState.ACTIVE);

        Assert.Equal(3, transitions.Count);
        Assert.Equal((ExamState.INITIALIZING, ExamState.PREFLIGHT), transitions[0]);
        Assert.Equal((ExamState.PREFLIGHT, ExamState.READY), transitions[1]);
        Assert.Equal((ExamState.READY, ExamState.ACTIVE), transitions[2]);
    }

    [Fact]
    public void TransitionToCompleted_StopsTimerAndMarksCompleted()
    {
        _stateMachine.TransitionTo(ExamState.ACTIVE);
        _stateMachine.TransitionTo(ExamState.COMPLETED);

        Assert.Equal(ExamState.COMPLETED, _stateMachine.CurrentState);
        Assert.False(_stateMachine.Timer.IsRunning);
        Assert.True(_stateMachine.CurrentSession.IsCompleted);

        // Completed session should not be loaded as active
        Assert.Null(_store.LoadActiveSession());
    }

    public void Dispose()
    {
        try
        {
            _stateMachine.Timer.Dispose();
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }
}
