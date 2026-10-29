using System;

namespace Klyro.Core.Security.RemoteAccess;

/// <summary>
/// Dedicated detector for Windows Remote Desktop Services and terminal sessions (Detection Layer A).
/// Utilizes GetSystemMetrics and WTSQuerySessionInformation APIs.
/// </summary>
public sealed class RemoteSessionDetector
{
    private readonly ISessionQueryProvider _queryProvider;
    private readonly bool _allowDisconnectedSessions;

    public RemoteSessionDetector(ISessionQueryProvider? queryProvider = null, bool allowDisconnectedSessions = false)
    {
        _queryProvider = queryProvider ?? new WindowsSessionQueryProvider();
        _allowDisconnectedSessions = allowDisconnectedSessions;
    }

    public RemoteAccessDetectionResult Evaluate()
    {
        try
        {
            bool metricRemoteSession = _queryProvider.IsRemoteSessionMetricSet();
            bool metricRemoteControl = _queryProvider.IsRemoteControlMetricSet();
            var wtsInfo = _queryProvider.GetCurrentWtsSessionInfo();

            if (!wtsInfo.QuerySucceeded && wtsInfo.ErrorMessage != null)
            {
                // If API failed critically
                return RemoteAccessDetectionResult.Unavailable($"Failed to query session information: {wtsInfo.ErrorMessage}");
            }

            // 1. Remote Shadowing / Remote Control check:
            // If the session is actively being shadowed or remotely controlled (SM_REMOTECONTROL or WTSShadow),
            // this is an immediate confirmed violation, even if attached to physical console.
            if (metricRemoteControl || wtsInfo.IsShadowed)
            {
                string protocolName = wtsInfo.ProtocolType switch
                {
                    1 => "Citrix ICA",
                    2 => "Microsoft RDP",
                    _ => "Remote Desktop Protocol"
                };

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.WindowsRemoteDesktop,
                    Classification = EvidenceClassification.ActiveRemoteSession,
                    Details = $"Active remote control or session shadowing ({protocolName}) detected on this workstation.",
                    ClientName = wtsInfo.ClientName,
                    ProtocolType = wtsInfo.ProtocolType,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            // 2. Physical Console Station:
            // When the session is running on the local physical console ("Console" winstation or %SESSIONNAME% == "Console"):
            // Even if an earlier RDP session left lingering metadata (stale SM_REMOTESESSION, stale ClientName, or ProtocolType 2),
            // the workstation is currently operating locally on the physical display adapter and not streaming over RDP.
            if (wtsInfo.IsConsoleStation)
            {
                return RemoteAccessDetectionResult.ClearResult();
            }

            // 3. Disconnected Session state:
            // If the connection state is explicitly Disconnected (WTSDisconnected = 4):
            // The RDP client has closed/disconnected its stream.
            if (wtsInfo.IsDisconnected)
            {
                if (_allowDisconnectedSessions)
                {
                    return RemoteAccessDetectionResult.ClearResult();
                }

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.WindowsRemoteDesktop,
                    Classification = EvidenceClassification.DisconnectedSession,
                    Details = "A disconnected remote desktop session was detected. For a secure exam environment, please log in at the physical console or sign out of Windows completely.",
                    ClientName = wtsInfo.ClientName,
                    ProtocolType = wtsInfo.ProtocolType,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            // 4. Active Remote Session (RDP / ICA / Non-console station):
            // An active remote connection is confirmed when:
            // - The WinStation is remote (e.g. RDP-Tcp#1) OR remote session metric is set
            // - AND protocol is RDP/ICA or client name is present
            bool isRdpProtocol = wtsInfo.ProtocolType is 1 or 2; // 1 = ICA, 2 = RDP
            bool hasClientName = !string.IsNullOrWhiteSpace(wtsInfo.ClientName);
            bool isRemoteStation = !string.IsNullOrWhiteSpace(wtsInfo.WinStationName) &&
                                  !wtsInfo.WinStationName.Equals("Console", StringComparison.OrdinalIgnoreCase);

            if (isRemoteStation || (isRdpProtocol && hasClientName) || (metricRemoteSession && isRdpProtocol) || (metricRemoteSession && !wtsInfo.IsConsoleStation))
            {
                string protocolName = wtsInfo.ProtocolType switch
                {
                    1 => "Citrix ICA",
                    2 => "Microsoft RDP",
                    _ => "Remote Desktop Protocol"
                };

                string clientDesc = hasClientName ? $"Client: {wtsInfo.ClientName}" : "Remote Client Connected";
                string stationDesc = !string.IsNullOrWhiteSpace(wtsInfo.WinStationName) ? $" ({wtsInfo.WinStationName})" : "";

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.WindowsRemoteDesktop,
                    Classification = EvidenceClassification.ActiveRemoteSession,
                    Details = $"Active {protocolName} session detected on this workstation ({clientDesc}{stationDesc}).",
                    ClientName = wtsInfo.ClientName,
                    ProtocolType = wtsInfo.ProtocolType,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            return RemoteAccessDetectionResult.ClearResult();
        }
        catch (Exception ex)
        {
            return RemoteAccessDetectionResult.Unavailable($"Session detection exception: {ex.Message}");
        }
    }
}
