using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class CaptivePortalDetectorTests
{
    private readonly CaptivePortalDetector _detector;
    private readonly NetworkOptions _options;

    public CaptivePortalDetectorTests()
    {
        _options = new NetworkOptions
        {
            PortalUrl = "http://172.16.103.254:8090/",
            FailureThreshold = 3
        };
        _detector = new CaptivePortalDetector(_options);
    }

    [Fact]
    public void AllProbesSucceed_ReturnsOnline()
    {
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5),
            CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10),
            Internet = ProbeResult.Success(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 204, 30),
            AssessmentProvider = ProbeResult.Success(ProbeType.AssessmentProvider, "https://www.codechef.com/", 200, 45)
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 0);
        Assert.Equal(NetworkState.ONLINE, state);
    }

    [Fact]
    public void GatewayFails_ReturnsOffline()
    {
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Failure(ProbeType.Gateway, "172.16.100.1", "Ping timed out"),
            CaptivePortal = ProbeResult.Failure(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", "Unreachable"),
            Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Unreachable"),
            AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "Unreachable")
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 5);
        Assert.Equal(NetworkState.OFFLINE, state);
    }

    [Fact]
    public void InternetFails_PortalUp_BelowThreshold_ReturnsDegraded()
    {
        // Consecutive failure 1 of 3: Do not show portal modal immediately to prevent false alarms
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5),
            CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10),
            Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Timeout", 0, 1500),
            AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "Timeout", 0, 1500)
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 1);
        Assert.Equal(NetworkState.DEGRADED, state);
    }

    [Fact]
    public void InternetFails_PortalUp_AtThreshold_ReturnsPortalRequired()
    {
        // Consecutive failures reach 3: Confirmed captive portal intercept
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5),
            CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10),
            Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Redirected (302)", 302, 15),
            AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "Timeout", 0, 1500)
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 3);
        Assert.Equal(NetworkState.PORTAL_REQUIRED, state);
    }

    [Fact]
    public void InternetSucceeds_CodeChefFails_ReturnsAssessmentServiceUnavailable()
    {
        // Internet is working (Google 204 succeeds), but CodeChef returns 503
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5),
            CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", 200, 10),
            Internet = ProbeResult.Success(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 204, 35),
            AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "HTTP 503", 503, 80)
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 0);
        Assert.Equal(NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE, state);
    }

    [Fact]
    public void GatewayUp_PortalDown_InternetDown_ReturnsDegraded()
    {
        // Campus uplink failure: Gateway is alive, but campus portal and internet cannot be reached
        var matrix = new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, "172.16.100.1", 200, 5),
            CaptivePortal = ProbeResult.Failure(ProbeType.CaptivePortal, "http://172.16.103.254:8090/", "Connection refused"),
            Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", "Connection refused"),
            AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "https://www.codechef.com/", "Connection refused")
        };

        var state = _detector.Evaluate(matrix, consecutiveInternetFailures: 5);
        Assert.Equal(NetworkState.DEGRADED, state);
    }
}
