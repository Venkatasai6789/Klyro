using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Klyro.Core.Recovery;
using Xunit;

namespace Klyro.RecoveryTests;

public class RecoveryControllerTests
{
    private sealed class MockNetworkMonitor : INetworkMonitor
    {
        public NetworkState CurrentState { get; set; } = NetworkState.STARTING;
        public NetworkStateMachine StateMachine { get; }
        public INetworkSimulator Simulator { get; } = new NetworkSimulator();

        public event NetworkStateChangedEventHandler? StateChanged;
        public event EventHandler<ProbeMatrix>? ProbeCompleted;

        public NetworkState ImmediateProbeResult { get; set; } = NetworkState.ONLINE;
        public long LastLatencyMs { get; set; } = 25;
        public string ConnectionSpeedTier { get; set; } = "Fast";

        public MockNetworkMonitor()
        {
            StateMachine = new NetworkStateMachine(new NetworkOptions());
        }

        public void SimulateStateChange(NetworkState oldState, NetworkState newState)
        {
            CurrentState = newState;
            var matrix = new ProbeMatrix();
            StateChanged?.Invoke(oldState, newState, matrix);
        }

        public void Start() { }
        public void Stop() { }

        public Task<NetworkState> TriggerImmediateProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ImmediateProbeResult);
        }

        public void Dispose() { }
    }

    [Fact]
    public void PortalRequired_TransitionsRecoveryToLoginRequired()
    {
        var mockMonitor = new MockNetworkMonitor();
        var options = new NetworkOptions();
        using var controller = new RecoveryController(mockMonitor, options);

        Assert.Equal(RecoveryState.Hidden, controller.CurrentState);

        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);

        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);
    }

    [Fact]
    public async Task HandleCredentialsSubmitted_Success_TransitionsToRestoredThenActiveInBackground_AndFiresEvent()
    {
        var mockMonitor = new MockNetworkMonitor { ImmediateProbeResult = NetworkState.ONLINE };
        var options = new NetworkOptions { RecoveryTimeoutMs = 5000 };
        using var controller = new RecoveryController(mockMonitor, options);

        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);
        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);

        bool resumeFired = false;
        controller.ExamResumeRequested += (s, e) => resumeFired = true;

        bool success = await controller.HandleCredentialsSubmittedAsync();

        Assert.True(success);
        Assert.Equal(RecoveryState.ActiveInBackground, controller.CurrentState);
        Assert.True(resumeFired);
    }

    [Fact]
    public async Task HandleCredentialsSubmitted_Failure_TransitionsToRecoveryFailed()
    {
        var mockMonitor = new MockNetworkMonitor { ImmediateProbeResult = NetworkState.PORTAL_REQUIRED };
        var options = new NetworkOptions { RecoveryTimeoutMs = 1500 }; // short timeout for test
        using var controller = new RecoveryController(mockMonitor, options);

        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);

        bool success = await controller.HandleCredentialsSubmittedAsync();

        Assert.False(success);
        Assert.Equal(RecoveryState.RecoveryFailed, controller.CurrentState);

        // Student clicks Retry -> back to LoginRequired
        controller.Retry();
        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);
    }

    [Fact]
    public void SpontaneousNetworkRestoration_KeepsSessionActiveInBackground()
    {
        var mockMonitor = new MockNetworkMonitor();
        var options = new NetworkOptions();
        using var controller = new RecoveryController(mockMonitor, options);

        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);
        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);

        bool resumeFired = false;
        controller.ExamResumeRequested += (s, e) => resumeFired = true;

        // Network recovers spontaneously (e.g. backend DHCP re-leased)
        mockMonitor.SimulateStateChange(NetworkState.PORTAL_REQUIRED, NetworkState.ONLINE);

        Assert.Equal(RecoveryState.ActiveInBackground, controller.CurrentState);
        Assert.True(resumeFired);
    }

    [Fact]
    public void KeepActiveInBackground_TransitionsToActiveInBackground_AndFiresResume()
    {
        var mockMonitor = new MockNetworkMonitor();
        var options = new NetworkOptions();
        using var controller = new RecoveryController(mockMonitor, options);

        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);
        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);

        bool resumeFired = false;
        controller.ExamResumeRequested += (s, e) => resumeFired = true;

        controller.KeepActiveInBackground();

        Assert.Equal(RecoveryState.ActiveInBackground, controller.CurrentState);
        Assert.True(resumeFired);
    }

    [Fact]
    public void PortalRequired_WhenActiveInBackground_ReactivatesLoginRequired()
    {
        var mockMonitor = new MockNetworkMonitor();
        var options = new NetworkOptions();
        using var controller = new RecoveryController(mockMonitor, options);

        // Session was previously active in background
        controller.KeepActiveInBackground();
        Assert.Equal(RecoveryState.ActiveInBackground, controller.CurrentState);

        // Session lease expires in middle of exam
        mockMonitor.SimulateStateChange(NetworkState.ONLINE, NetworkState.PORTAL_REQUIRED);

        Assert.Equal(RecoveryState.LoginRequired, controller.CurrentState);
    }
}
