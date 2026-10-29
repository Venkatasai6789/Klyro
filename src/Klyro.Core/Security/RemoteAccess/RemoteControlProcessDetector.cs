using System;
using System.Collections.Generic;
using System.Linq;

namespace Klyro.Core.Security.RemoteAccess;

/// <summary>
/// Prohibited remote-control and screen-sharing application detector (Detection Layer B).
/// Distinguishes between interactive remote sessions, background service daemons in Session 0,
/// and legitimate accessibility software.
/// </summary>
public sealed class RemoteControlProcessDetector
{
    private static readonly HashSet<string> InteractiveRemoteControlApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "anydesk",
        "teamviewer",
        "ultraviewer",
        "rustdesk",
        "winvnc",
        "vncserver",
        "tvnserver",
        "srfeature",
        "strwinclt",
        "logmein",
        "parsec",
        "remoting_desktop",                 // Chrome Remote Desktop interactive session agent
        "remote_assistance_host",          // Chrome Remote Desktop assistance host
        "remote_assistance_host_uiaccess", // Chrome Remote Desktop elevated UI host
        "remoting_start_host",             // Chrome Remote Desktop startup host
        "quickassist",                     // Windows Quick Assist
        "msra",                            // Windows Remote Assistance
        "splashtop",                       // Splashtop Personal/Business
        "splashtopstreamer",
        "ammyy",                           // Ammyy Admin
        "supremo",                         // Supremo Remote Desktop
        "aeroadmin",                       // AeroAdmin
        "tightvnc",
        "realvnc"
    };

    private static readonly HashSet<string> RemoteServiceDaemons = new(StringComparer.OrdinalIgnoreCase)
    {
        "remoting_host",                    // Chrome Remote Desktop host
        "remoting_native_messaging_host",   // Chrome Remote Desktop helper
        "teamviewer_service",
        "ultraviewer_service"
    };

    private static readonly HashSet<string> KnownScreenSharingApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64",
        "obs"
    };

    private static readonly HashSet<string> DefaultAccessibilityWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvda",
        "jaws",
        "narrator",
        "magnify",
        "osk",
        "displayswitch"
    };

    private readonly IProcessProvider _processProvider;
    private readonly HashSet<string> _prohibitedRemoteApps;
    private readonly HashSet<string> _accessibilityWhitelist;

    public RemoteControlProcessDetector(
        IProcessProvider? processProvider = null,
        IEnumerable<string>? additionalBlocked = null,
        IEnumerable<string>? accessibilityWhitelist = null)
    {
        _processProvider = processProvider ?? new SystemProcessProvider();
        _prohibitedRemoteApps = new HashSet<string>(InteractiveRemoteControlApps, StringComparer.OrdinalIgnoreCase);
        if (additionalBlocked != null)
        {
            foreach (var app in additionalBlocked)
            {
                if (!string.IsNullOrWhiteSpace(app)) _prohibitedRemoteApps.Add(app.Trim());
            }
        }

        _accessibilityWhitelist = new HashSet<string>(DefaultAccessibilityWhitelist, StringComparer.OrdinalIgnoreCase);
        if (accessibilityWhitelist != null)
        {
            foreach (var app in accessibilityWhitelist)
            {
                if (!string.IsNullOrWhiteSpace(app)) _accessibilityWhitelist.Add(app.Trim());
            }
        }
    }

    public RemoteAccessDetectionResult Evaluate(bool hasActiveRemoteSessionMetric = false)
    {
        try
        {
            var running = _processProvider.GetRunningProcesses();
            int userSessionId = _processProvider.CurrentSessionId;

            var detectedInteractive = new List<string>();
            var detectedSessionDaemons = new List<string>();
            var detectedSharing = new List<string>();

            foreach (var proc in running)
            {
                string name = proc.ProcessName;

                // Never flag assistive technology
                if (_accessibilityWhitelist.Contains(name))
                {
                    continue;
                }

                bool isUserSession = (proc.SessionId == userSessionId || proc.SessionId != 0 || userSessionId == 0);

                if (_prohibitedRemoteApps.Contains(name))
                {
                    if (isUserSession)
                    {
                        detectedInteractive.Add(name);
                    }
                    else
                    {
                        detectedSessionDaemons.Add(name);
                    }
                }
                else if (RemoteServiceDaemons.Contains(name))
                {
                    // A remote daemon running inside user desktop session indicates active remoting
                    if (proc.SessionId == userSessionId && userSessionId > 0)
                    {
                        detectedInteractive.Add(name);
                    }
                    else
                    {
                        detectedSessionDaemons.Add(name);
                    }
                }
                else if (KnownScreenSharingApps.Contains(name))
                {
                    if (isUserSession)
                    {
                        detectedSharing.Add(name);
                    }
                }
            }

            // 1. Interactive remote tools running in candidate desktop are confirmed violations
            if (detectedInteractive.Count > 0)
            {
                var unique = detectedInteractive.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string names = string.Join(", ", unique);

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.RemoteControlSoftware,
                    Classification = EvidenceClassification.ActiveProcess,
                    Details = $"Active remote access application running on this workstation: {names}.",
                    DetectedProcesses = unique,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            // 2. Corroborated daemons (if active remote session metric or remote connection is present)
            if (hasActiveRemoteSessionMetric && detectedSessionDaemons.Count > 0)
            {
                var unique = detectedSessionDaemons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string names = string.Join(", ", unique);

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.RemoteControlSoftware,
                    Classification = EvidenceClassification.CorroboratedViolation,
                    Details = $"Active remote desktop connection corroborated by remote service: {names}.",
                    DetectedProcesses = unique,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            // 3. Screen sharing tools
            if (detectedSharing.Count > 0)
            {
                var unique = detectedSharing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string names = string.Join(", ", unique);

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.ScreenSharing,
                    Classification = EvidenceClassification.ActiveProcess,
                    Details = $"Prohibited screen broadcast or sharing application detected: {names}.",
                    DetectedProcesses = unique,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            // 4. Background service daemons running in Session 0 without corroborated remote session
            if (detectedSessionDaemons.Count > 0)
            {
                var unique = detectedSessionDaemons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string names = string.Join(", ", unique);

                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ReviewRequired,
                    Category = DetectionCategory.RemoteControlSoftware,
                    Classification = EvidenceClassification.BackgroundService,
                    Details = $"Background remote service daemon running in Session 0: {names}. Manual review recommended.",
                    DetectedProcesses = unique,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            return RemoteAccessDetectionResult.ClearResult();
        }
        catch (Exception ex)
        {
            return RemoteAccessDetectionResult.Unavailable($"Process scan error: {ex.Message}");
        }
    }
}
