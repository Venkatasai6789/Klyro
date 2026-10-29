using System;
using System.Collections.Generic;
using System.Linq;

namespace Klyro.Core.Security.RemoteAccess;

public enum PolicyEnforcementAction
{
    Permit,
    BlockEntry,
    RestrictActiveExam,
    RequireAdminReview,
    Recheck
}

public interface ISecurityPolicyEvaluator
{
    RemoteAccessDetectionResult EvaluateCompliance();
    PolicyEnforcementAction DetermineAction(RemoteAccessDetectionResult result, bool isExamActive);
}

/// <summary>
/// Authoritative evaluator for environment security policy.
/// Synthesizes Windows remote session signals, remote-control processes, and accessibility exemptions.
/// </summary>
public sealed class SecurityPolicyEvaluator : ISecurityPolicyEvaluator
{
    private readonly RemoteSessionDetector _sessionDetector;
    private readonly RemoteControlProcessDetector _processDetector;
    private readonly WindowBannerDetector _bannerDetector;
    private readonly bool _strictDaemonBlocking;

    public SecurityPolicyEvaluator(
        RemoteSessionDetector? sessionDetector = null,
        RemoteControlProcessDetector? processDetector = null,
        WindowBannerDetector? bannerDetector = null,
        bool strictDaemonBlocking = false)
    {
        _sessionDetector = sessionDetector ?? new RemoteSessionDetector();
        _processDetector = processDetector ?? new RemoteControlProcessDetector();
        _bannerDetector = bannerDetector ?? new WindowBannerDetector();
        _strictDaemonBlocking = strictDaemonBlocking;
    }

    public RemoteAccessDetectionResult EvaluateCompliance()
    {
        // 1. Layer A: Windows Remote Sessions (RDP / Terminal Services)
        var sessionResult = _sessionDetector.Evaluate();
        if (sessionResult.Decision == SecurityPolicyDecision.ConfirmedViolation)
        {
            return sessionResult;
        }

        // 2. Layer B: Prohibited Remote Control Applications
        bool hasSessionMetric = sessionResult.Category == DetectionCategory.WindowsRemoteDesktop;
        var processResult = _processDetector.Evaluate(hasActiveRemoteSessionMetric: hasSessionMetric);
        if (processResult.Decision == SecurityPolicyDecision.ConfirmedViolation)
        {
            return processResult;
        }

        // 3. Layer C: Active Window Banners & Screen Sharing Indicators
        var bannerResult = _bannerDetector.Evaluate();
        if (bannerResult.Decision == SecurityPolicyDecision.ConfirmedViolation)
        {
            return bannerResult;
        }

        // 4. Monitor availability check
        if (sessionResult.Decision == SecurityPolicyDecision.MonitorUnavailable)
        {
            return sessionResult;
        }

        if (processResult.Decision == SecurityPolicyDecision.MonitorUnavailable)
        {
            return processResult;
        }

        if (bannerResult.Decision == SecurityPolicyDecision.MonitorUnavailable)
        {
            return bannerResult;
        }

        // 5. Background service or ambiguous signals
        if (processResult.Decision == SecurityPolicyDecision.ReviewRequired ||
            sessionResult.Decision == SecurityPolicyDecision.ReviewRequired)
        {
            if (_strictDaemonBlocking)
            {
                return new RemoteAccessDetectionResult
                {
                    Decision = SecurityPolicyDecision.ConfirmedViolation,
                    Category = DetectionCategory.RemoteControlSoftware,
                    Classification = EvidenceClassification.BackgroundService,
                    Details = processResult.Details,
                    DetectedProcesses = processResult.DetectedProcesses,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            return processResult.Decision == SecurityPolicyDecision.ReviewRequired ? processResult : sessionResult;
        }

        return RemoteAccessDetectionResult.ClearResult();
    }

    public PolicyEnforcementAction DetermineAction(RemoteAccessDetectionResult result, bool isExamActive)
    {
        switch (result.Decision)
        {
            case SecurityPolicyDecision.ConfirmedViolation:
                return isExamActive ? PolicyEnforcementAction.RestrictActiveExam : PolicyEnforcementAction.BlockEntry;

            case SecurityPolicyDecision.MonitorUnavailable:
                return isExamActive ? PolicyEnforcementAction.RestrictActiveExam : PolicyEnforcementAction.BlockEntry;

            case SecurityPolicyDecision.ReviewRequired:
            case SecurityPolicyDecision.Suspicious:
                return PolicyEnforcementAction.RequireAdminReview;

            case SecurityPolicyDecision.Clear:
            default:
                return PolicyEnforcementAction.Permit;
        }
    }
}
