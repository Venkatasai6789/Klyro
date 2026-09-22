using System;
using Klyro.Core.Configuration;

namespace Klyro.Core.Network;

/// <summary>
/// Aggregated snapshot of all active connectivity probes.
/// </summary>
public sealed class ProbeMatrix
{
    public ProbeResult Gateway { get; init; } = ProbeResult.Failure(ProbeType.Gateway, "gateway", "Uninitialized");
    public ProbeResult CaptivePortal { get; init; } = ProbeResult.Failure(ProbeType.CaptivePortal, "portal", "Uninitialized");
    public ProbeResult Internet { get; init; } = ProbeResult.Failure(ProbeType.InternetConnectivity, "internet", "Uninitialized");
    public ProbeResult AssessmentProvider { get; init; } = ProbeResult.Failure(ProbeType.AssessmentProvider, "assessment", "Uninitialized");
}

/// <summary>
/// Evaluates multi-signal probe matrices to determine exact network state.
/// </summary>
public sealed class CaptivePortalDetector
{
    private readonly NetworkOptions _networkOptions;

    public CaptivePortalDetector(NetworkOptions networkOptions)
    {
        _networkOptions = networkOptions ?? throw new ArgumentNullException(nameof(networkOptions));
    }

    /// <summary>
    /// Evaluates the probe matrix and consecutive failure count to produce the current NetworkState.
    /// </summary>
    public NetworkState Evaluate(ProbeMatrix matrix, int consecutiveInternetFailures)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        // Signal 1: Local gateway / physical link
        if (!matrix.Gateway.IsSuccess)
        {
            return NetworkState.OFFLINE;
        }

        // Signal 2: Global Internet connectivity (e.g. HTTP 204)
        if (matrix.Internet.IsSuccess)
        {
            // Internet is fully functional. Check if CodeChef assessment platform is reachable.
            if (matrix.AssessmentProvider.IsSuccess)
            {
                return NetworkState.ONLINE;
            }

            // Distinguish CodeChef outage from Wi-Fi issues:
            // Internet works, but CodeChef is failing!
            return NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE;
        }

        // Signal 3: Internet is failing. Evaluate if Captive Portal is active.
        if (matrix.CaptivePortal.IsSuccess)
        {
            // Avoid flapping on momentary wireless jitter: require failureThreshold (default: 3) consecutive failures
            if (consecutiveInternetFailures >= _networkOptions.FailureThreshold)
            {
                return NetworkState.PORTAL_REQUIRED;
            }

            return NetworkState.DEGRADED;
        }

        // Gateway is alive, but neither Internet nor Portal can be reached (e.g. uplink down)
        return NetworkState.DEGRADED;
    }
}
