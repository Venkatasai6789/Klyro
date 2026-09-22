using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class NetworkMonitorLatencyTests
{
    private sealed class MockProbe : IConnectivityProbe
    {
        public ProbeResult GatewayResult { get; set; } = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5);
        public ProbeResult CaptivePortalResult { get; set; } = ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10);
        public ProbeResult InternetResult { get; set; } = ProbeResult.Success(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 204, 35);
        public ProbeResult AssessmentResult { get; set; } = ProbeResult.Success(ProbeType.AssessmentProvider, "https://www.codechef.com/", 200, 40);

        public Task<ProbeResult> ExecuteAsync(ProbeType probeType, string targetEndpoint, int timeoutMs, CancellationToken cancellationToken = default)
        {
            return probeType switch
            {
                ProbeType.Gateway => Task.FromResult(GatewayResult),
                ProbeType.CaptivePortal => Task.FromResult(CaptivePortalResult),
                ProbeType.InternetConnectivity => Task.FromResult(InternetResult),
                ProbeType.AssessmentProvider => Task.FromResult(AssessmentResult),
                _ => Task.FromResult(ProbeResult.Failure(probeType, targetEndpoint, "Unknown"))
            };
        }
    }

    [Theory]
    [InlineData(35, "Fast")]
    [InlineData(79, "Fast")]
    [InlineData(80, "Good")]
    [InlineData(150, "Good")]
    [InlineData(249, "Good")]
    [InlineData(250, "Slow")]
    [InlineData(500, "Slow")]
    public async Task TriggerImmediateProbe_CalculatesCorrectSpeedTier(long latencyMs, string expectedTier)
    {
        var mockProbe = new MockProbe
        {
            InternetResult = ProbeResult.Success(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 204, latencyMs)
        };

        var config = new KlyroConfiguration();
        config.Network.EnableNetworkSimulator = false;

        using var monitor = new NetworkMonitor(config, mockProbe);

        var state = await monitor.TriggerImmediateProbeAsync();

        Assert.Equal(NetworkState.ONLINE, state);
        Assert.Equal(latencyMs, monitor.LastLatencyMs);
        Assert.Equal(expectedTier, monitor.ConnectionSpeedTier);
    }

    [Fact]
    public async Task TriggerImmediateProbe_WhenInternetFails_SetsSpeedTierToOffline()
    {
        var mockProbe = new MockProbe
        {
            InternetResult = ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Blocked")
        };

        var config = new KlyroConfiguration();
        config.Network.EnableNetworkSimulator = false;

        using var monitor = new NetworkMonitor(config, mockProbe);

        await monitor.TriggerImmediateProbeAsync();

        Assert.Equal("Offline", monitor.ConnectionSpeedTier);
    }

    [Fact]
    public async Task TriggerImmediateProbe_FiresProbeCompletedEvent()
    {
        var mockProbe = new MockProbe();
        var config = new KlyroConfiguration();
        config.Network.EnableNetworkSimulator = false;

        using var monitor = new NetworkMonitor(config, mockProbe);

        ProbeMatrix? receivedMatrix = null;
        monitor.ProbeCompleted += (s, matrix) => receivedMatrix = matrix;

        await monitor.TriggerImmediateProbeAsync();

        Assert.NotNull(receivedMatrix);
        Assert.True(receivedMatrix.Internet.IsSuccess);
    }
}
