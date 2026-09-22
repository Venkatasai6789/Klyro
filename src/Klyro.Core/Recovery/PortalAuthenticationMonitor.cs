using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Network;

namespace Klyro.Core.Recovery;

/// <summary>
/// Monitors and independently verifies network and assessment restoration after portal login submission.
/// </summary>
public sealed class PortalAuthenticationMonitor
{
    private readonly INetworkMonitor _networkMonitor;
    private readonly NetworkOptions _networkOptions;
    private readonly IAuditLogger? _auditLogger;

    public PortalAuthenticationMonitor(
        INetworkMonitor networkMonitor,
        NetworkOptions networkOptions,
        IAuditLogger? auditLogger = null)
    {
        _networkMonitor = networkMonitor ?? throw new ArgumentNullException(nameof(networkMonitor));
        _networkOptions = networkOptions ?? throw new ArgumentNullException(nameof(networkOptions));
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Repeatedly probes the network following a portal login submission until connectivity
    /// is verified or the bounded recovery timeout expires.
    /// </summary>
    public async Task<bool> VerifyConnectionRestoredAsync(CancellationToken cancellationToken = default)
    {
        _auditLogger?.Log(
            EventType.PORTAL_AUTH_STARTED,
            "Authenticating",
            "Portal credentials submitted. Commencing independent connectivity verification.");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_networkOptions.RecoveryTimeoutMs));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        const int retryDelayMs = 1000;

        while (!linkedCts.Token.IsCancellationRequested)
        {
            try
            {
                var state = await _networkMonitor.TriggerImmediateProbeAsync(linkedCts.Token);

                if (state == NetworkState.ONLINE)
                {
                    _auditLogger?.Log(
                        EventType.PORTAL_AUTH_SUCCESS,
                        "ConnectionRestored",
                        "Network authentication verified. Global Internet and CodeChef assessment endpoints reachable.");
                    return true;
                }

                await Task.Delay(retryDelayMs, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(
                    EventType.PORTAL_AUTH_FAILED,
                    "RecoveryFailed",
                    $"Error during portal authentication verification: {ex.Message}");
            }
        }

        _auditLogger?.Log(
            EventType.PORTAL_AUTH_FAILED,
            "RecoveryFailed",
            $"Portal authentication verification timed out after {_networkOptions.RecoveryTimeoutMs}ms without Internet restoration.");

        return false;
    }
}
