using System;
using Klyro.Core.Exam;
using Xunit;

namespace Klyro.UnitTests;

public class ExamTimerTests
{
    [Fact]
    public void TimerInitialization_HasCorrectDurationAndNotRunning()
    {
        var duration = TimeSpan.FromMinutes(60);
        using var timer = new ExamTimer(duration);

        Assert.Equal(duration, timer.TotalDuration);
        Assert.Equal(duration, timer.RemainingTime);
        Assert.Equal(TimeSpan.Zero, timer.ElapsedTime);
        Assert.False(timer.IsRunning);
        Assert.False(timer.IsPaused);
    }

    [Fact]
    public void InvalidDuration_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExamTimer(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExamTimer(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void Start_SetsIsRunningTrue()
    {
        using var timer = new ExamTimer(TimeSpan.FromMinutes(10));
        timer.Start();

        Assert.True(timer.IsRunning);
        Assert.False(timer.IsPaused);
    }

    [Fact]
    public void PauseAndResume_UpdatesIsPausedState()
    {
        using var timer = new ExamTimer(TimeSpan.FromMinutes(10));
        timer.Start();

        bool pausedFired = false;
        bool resumedFired = false;
        timer.Paused += (s, e) => pausedFired = true;
        timer.Resumed += (s, e) => resumedFired = true;

        timer.Pause();
        Assert.True(timer.IsPaused);
        Assert.True(pausedFired);

        timer.Resume();
        Assert.False(timer.IsPaused);
        Assert.True(resumedFired);
    }

    [Fact]
    public void NetworkRecoveryPolicy_PausePolicy_PausesTimer()
    {
        using var timer = new ExamTimer(TimeSpan.FromMinutes(10), "PauseDuringVerifiedNetworkFailure");
        timer.Start();

        timer.OnNetworkRecoveryStateChanged(isRecovering: true);
        Assert.True(timer.IsPaused);

        timer.OnNetworkRecoveryStateChanged(isRecovering: false);
        Assert.False(timer.IsPaused);
    }

    [Fact]
    public void NetworkRecoveryPolicy_ContinuePolicy_DoesNotPauseTimer()
    {
        using var timer = new ExamTimer(TimeSpan.FromMinutes(10), "ContinueDuringNetworkFailure");
        timer.Start();

        timer.OnNetworkRecoveryStateChanged(isRecovering: true);
        Assert.False(timer.IsPaused); // Timer keeps running
    }
}
