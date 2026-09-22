using System;

namespace Klyro.Core.Network;

/// <summary>
/// Development and test harness simulator for network faults and recovery events.
/// </summary>
public sealed class NetworkSimulator : INetworkSimulator
{
    private int _flapCounter;

    public bool IsSimulationActive { get; set; } = false;
    public SimulatedNetworkCondition CurrentCondition { get; set; } = SimulatedNetworkCondition.Normal;

    public ProbeMatrix GenerateSimulatedMatrix(string gatewayEndpoint, string portalEndpoint, string internetEndpoint, string assessmentEndpoint)
    {
        return CurrentCondition switch
        {
            SimulatedNetworkCondition.Normal => new ProbeMatrix
            {
                Gateway = ProbeResult.Success(ProbeType.Gateway, gatewayEndpoint, 200, 5),
                CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, portalEndpoint, 200, 15),
                Internet = ProbeResult.Success(ProbeType.InternetConnectivity, internetEndpoint, 204, 45),
                AssessmentProvider = ProbeResult.Success(ProbeType.AssessmentProvider, assessmentEndpoint, 200, 60)
            },

            SimulatedNetworkCondition.PortalRequired => new ProbeMatrix
            {
                Gateway = ProbeResult.Success(ProbeType.Gateway, gatewayEndpoint, 200, 5),
                CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, portalEndpoint, 200, 12),
                Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, internetEndpoint, "Captive portal redirect (302)", 302, 18),
                AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, assessmentEndpoint, "Connection timed out", 0, 1500)
            },

            SimulatedNetworkCondition.Offline => new ProbeMatrix
            {
                Gateway = ProbeResult.Failure(ProbeType.Gateway, gatewayEndpoint, "Default gateway unreachable / adapter disconnected"),
                CaptivePortal = ProbeResult.Failure(ProbeType.CaptivePortal, portalEndpoint, "Host unreachable"),
                Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, internetEndpoint, "Host unreachable"),
                AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, assessmentEndpoint, "Host unreachable")
            },

            SimulatedNetworkCondition.Degraded => new ProbeMatrix
            {
                Gateway = ProbeResult.Success(ProbeType.Gateway, gatewayEndpoint, 200, 120),
                CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, portalEndpoint, 200, 250),
                Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, internetEndpoint, "Packet dropped / timeout", 0, 1500),
                AssessmentProvider = ProbeResult.Success(ProbeType.AssessmentProvider, assessmentEndpoint, 200, 1400)
            },

            SimulatedNetworkCondition.CodeChefUnavailable => new ProbeMatrix
            {
                Gateway = ProbeResult.Success(ProbeType.Gateway, gatewayEndpoint, 200, 5),
                CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, portalEndpoint, 200, 15),
                Internet = ProbeResult.Success(ProbeType.InternetConnectivity, internetEndpoint, 204, 42),
                AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, assessmentEndpoint, "HTTP 503 Service Unavailable", 503, 110)
            },

            SimulatedNetworkCondition.PortalUnavailable => new ProbeMatrix
            {
                Gateway = ProbeResult.Success(ProbeType.Gateway, gatewayEndpoint, 200, 5),
                CaptivePortal = ProbeResult.Failure(ProbeType.CaptivePortal, portalEndpoint, "HTTP 500 Internal Server Error", 500, 200),
                Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, internetEndpoint, "No internet route"),
                AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, assessmentEndpoint, "No route")
            },

            SimulatedNetworkCondition.Flapping => GenerateFlappingMatrix(gatewayEndpoint, portalEndpoint, internetEndpoint, assessmentEndpoint),

            _ => throw new ArgumentOutOfRangeException(nameof(CurrentCondition))
        };
    }

    private ProbeMatrix GenerateFlappingMatrix(string gateway, string portal, string internet, string assessment)
    {
        _flapCounter++;
        bool isUp = _flapCounter % 2 == 1;

        return new ProbeMatrix
        {
            Gateway = ProbeResult.Success(ProbeType.Gateway, gateway, 200, 10),
            CaptivePortal = ProbeResult.Success(ProbeType.CaptivePortal, portal, 200, 15),
            Internet = isUp
                ? ProbeResult.Success(ProbeType.InternetConnectivity, internet, 204, 50)
                : ProbeResult.Failure(ProbeType.InternetConnectivity, internet, "Temporary packet loss", 0, 1000),
            AssessmentProvider = ProbeResult.Success(ProbeType.AssessmentProvider, assessment, 200, 65)
        };
    }
}
