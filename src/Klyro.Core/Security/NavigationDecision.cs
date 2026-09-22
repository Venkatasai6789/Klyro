using System;

namespace Klyro.Core.Security;

/// <summary>
/// Outcome of a security guard evaluation for a navigation, popup, or protocol attempt.
/// </summary>
public sealed class NavigationDecision
{
    public bool IsAllowed { get; }
    public string Reason { get; }
    public string? TargetUrl { get; }

    private NavigationDecision(bool isAllowed, string reason, string? targetUrl)
    {
        IsAllowed = isAllowed;
        Reason = reason;
        TargetUrl = targetUrl;
    }

    public static NavigationDecision Allow(string? targetUrl, string reason = "Origin present in allowlist") =>
        new(true, reason, targetUrl);

    public static NavigationDecision Block(string? targetUrl, string reason) =>
        new(false, reason, targetUrl);
}
