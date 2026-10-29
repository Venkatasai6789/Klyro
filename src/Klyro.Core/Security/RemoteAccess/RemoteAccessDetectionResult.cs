using System;
using System.Collections.Generic;

namespace Klyro.Core.Security.RemoteAccess;

public enum DetectionCategory
{
    None,
    WindowsRemoteDesktop,
    RemoteControlSoftware,
    ScreenSharing,
    VirtualDisplay
}

public enum EvidenceClassification
{
    None,
    InstalledOnly,
    BackgroundService,
    ActiveProcess,
    ActiveRemoteSession,
    DisconnectedSession,
    CorroboratedViolation
}

public enum SecurityPolicyDecision
{
    Clear,
    ReviewRequired,
    Suspicious,
    ConfirmedViolation,
    MonitorUnavailable
}

public sealed class RemoteAccessDetectionResult
{
    public SecurityPolicyDecision Decision { get; init; } = SecurityPolicyDecision.Clear;
    public DetectionCategory Category { get; init; } = DetectionCategory.None;
    public EvidenceClassification Classification { get; init; } = EvidenceClassification.None;
    public string Details { get; init; } = "No remote desktop or unauthorized remote access detected.";
    public int? SessionId { get; init; }
    public string? ClientName { get; init; }
    public ushort? ProtocolType { get; init; }
    public IReadOnlyList<string> DetectedProcesses { get; init; } = Array.Empty<string>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string PolicyVersion { get; init; } = "1.0.0";

    public bool IsViolation => Decision is SecurityPolicyDecision.ConfirmedViolation;
    public bool IsBlocked => Decision is SecurityPolicyDecision.ConfirmedViolation or SecurityPolicyDecision.MonitorUnavailable;

    public static RemoteAccessDetectionResult ClearResult() => new()
    {
        Decision = SecurityPolicyDecision.Clear,
        Category = DetectionCategory.None,
        Classification = EvidenceClassification.None,
        Details = "Environment clear: No remote desktop or remote access detected."
    };

    public static RemoteAccessDetectionResult Unavailable(string reason) => new()
    {
        Decision = SecurityPolicyDecision.MonitorUnavailable,
        Category = DetectionCategory.None,
        Classification = EvidenceClassification.None,
        Details = $"Security monitor unavailable: {reason}"
    };
}
