using System.Collections.Generic;
using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class NetworkStateMachineTests
{
    private readonly NetworkOptions _options;
    private readonly NetworkStateMachine _stateMachine;

    public NetworkStateMachineTests()
    {
        _options = new NetworkOptions
        {
            PortalUrl = "http://172.16.103.254:8090/",
            FailureThreshold = 3
        };
        _stateMachine = new NetworkStateMachine(_options);
    }

    [Fact]
    public void InitialState_IsStarting()
    {
        Assert.Equal(NetworkState.STARTING, _stateMachine.CurrentState);
        Assert.Equal(0, _stateMachine.ConsecutiveFailures);
    }

    [Fact]
    public void ProcessProbeMatrix_Online_UpdatesStateAndResetsFailures()
    {
        var onlineMatrix = CreateMatrix(gatewayUp: true, portalUp: true, internetUp: true, assessmentUp: true);

        var state = _stateMachine.ProcessProbeMatrix(onlineMatrix);

        Assert.Equal(NetworkState.ONLINE, state);
        Assert.Equal(NetworkState.ONLINE, _stateMachine.CurrentState);
        Assert.Equal(0, _stateMachine.ConsecutiveFailures);
        Assert.NotNull(_stateMachine.LastOnlineTime);
    }

    [Fact]
    public void ThresholdDamping_RequiresThreeFailuresForPortalRequired()
    {
        var onlineMatrix = CreateMatrix(gatewayUp: true, portalUp: true, internetUp: true, assessmentUp: true);
        var captiveMatrix = CreateMatrix(gatewayUp: true, portalUp: true, internetUp: false, assessmentUp: false);

        // Initial online
        _stateMachine.ProcessProbeMatrix(onlineMatrix);
        Assert.Equal(NetworkState.ONLINE, _stateMachine.CurrentState);

        // 1st failure -> DEGRADED
        var s1 = _stateMachine.ProcessProbeMatrix(captiveMatrix);
        Assert.Equal(NetworkState.DEGRADED, s1);
        Assert.Equal(1, _stateMachine.ConsecutiveFailures);

        // 2nd failure -> DEGRADED
        var s2 = _stateMachine.ProcessProbeMatrix(captiveMatrix);
        Assert.Equal(NetworkState.DEGRADED, s2);
        Assert.Equal(2, _stateMachine.ConsecutiveFailures);

        // 3rd failure -> PORTAL_REQUIRED
        var s3 = _stateMachine.ProcessProbeMatrix(captiveMatrix);
        Assert.Equal(NetworkState.PORTAL_REQUIRED, s3);
        Assert.Equal(3, _stateMachine.ConsecutiveFailures);

        // Recovery back to ONLINE
        var s4 = _stateMachine.ProcessProbeMatrix(onlineMatrix);
        Assert.Equal(NetworkState.ONLINE, s4);
        Assert.Equal(0, _stateMachine.ConsecutiveFailures);
    }

    [Fact]
    public void StateChangedEvent_FiresOnEveryTransition()
    {
        var transitions = new List<(NetworkState Old, NetworkState New)>();
        _stateMachine.StateChanged += (oldState, newState, matrix) =>
        {
            transitions.Add((oldState, newState));
        };

        var onlineMatrix = CreateMatrix(true, true, true, true);
        var offlineMatrix = CreateMatrix(false, false, false, false);

        _stateMachine.ProcessProbeMatrix(onlineMatrix);
        _stateMachine.ProcessProbeMatrix(offlineMatrix);

        Assert.Equal(2, transitions.Count);
        Assert.Equal((NetworkState.STARTING, NetworkState.ONLINE), transitions[0]);
        Assert.Equal((NetworkState.ONLINE, NetworkState.OFFLINE), transitions[1]);
    }

    private static ProbeMatrix CreateMatrix(bool gatewayUp, bool portalUp, bool internetUp, bool assessmentUp)
    {
        return new ProbeMatrix
        {
            Gateway = gatewayUp
                ? ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5)
                : ProbeResult.Failure(ProbeType.Gateway, "172.16.100.1", "Ping failed"),
            CaptivePortal = portalUp
                ? ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10)
                : ProbeResult.Failure(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", "Timeout"),
            Internet = internetUp
                ? ProbeResult.Success(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 204, 30)
                : ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Blocked"),
            AssessmentProvider = assessmentUp
                ? ProbeResult.Success(ProbeType.AssessmentProvider, "https://www.codechef.com/", 200, 45)
                : ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "Blocked")
        };
    }
}
