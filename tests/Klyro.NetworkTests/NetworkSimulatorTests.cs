using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class NetworkSimulatorTests
{
    private readonly NetworkSimulator _simulator = new();

    private const string Gateway = "172.16.100.1";
    private const string Portal = "http://172.16.103.254:8090/";
    private const string Internet = "http://clients3.google.com/generate_204";
    private const string Assessment = "https://www.codechef.com/";

    [Fact]
    public void NormalCondition_AllProbesPass()
    {
        _simulator.CurrentCondition = SimulatedNetworkCondition.Normal;
        var matrix = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);

        Assert.True(matrix.Gateway.IsSuccess);
        Assert.True(matrix.CaptivePortal.IsSuccess);
        Assert.True(matrix.Internet.IsSuccess);
        Assert.True(matrix.AssessmentProvider.IsSuccess);
        Assert.Equal(204, matrix.Internet.StatusCode);
    }

    [Fact]
    public void PortalRequiredCondition_InternetBlocked_PortalAvailable()
    {
        _simulator.CurrentCondition = SimulatedNetworkCondition.PortalRequired;
        var matrix = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);

        Assert.True(matrix.Gateway.IsSuccess);
        Assert.True(matrix.CaptivePortal.IsSuccess);
        Assert.False(matrix.Internet.IsSuccess);
        Assert.False(matrix.AssessmentProvider.IsSuccess);
    }

    [Fact]
    public void OfflineCondition_GatewayFails()
    {
        _simulator.CurrentCondition = SimulatedNetworkCondition.Offline;
        var matrix = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);

        Assert.False(matrix.Gateway.IsSuccess);
    }

    [Fact]
    public void CodeChefUnavailableCondition_Internet204Passes_AssessmentFails()
    {
        _simulator.CurrentCondition = SimulatedNetworkCondition.CodeChefUnavailable;
        var matrix = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);

        Assert.True(matrix.Gateway.IsSuccess);
        Assert.True(matrix.Internet.IsSuccess);
        Assert.Equal(204, matrix.Internet.StatusCode);
        Assert.False(matrix.AssessmentProvider.IsSuccess);
        Assert.Equal(503, matrix.AssessmentProvider.StatusCode);
    }

    [Fact]
    public void FlappingCondition_AlternatesSignals()
    {
        _simulator.CurrentCondition = SimulatedNetworkCondition.Flapping;

        var m1 = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);
        var m2 = _simulator.GenerateSimulatedMatrix(Gateway, Portal, Internet, Assessment);

        // Flapping alternates internet reachability
        Assert.NotEqual(m1.Internet.IsSuccess, m2.Internet.IsSuccess);
    }
}
