using System;
using Klyro.Core.Security;

namespace Klyro.Core.Browser;

/// <summary>
/// Evaluates and controls top-level browser navigation against strict allowlist rules.
/// </summary>
public sealed class NavigationGuard
{
    private readonly AllowlistManager _allowlistManager;

    public NavigationGuard(AllowlistManager allowlistManager)
    {
        _allowlistManager = allowlistManager ?? throw new ArgumentNullException(nameof(allowlistManager));
    }

    /// <summary>
    /// Checks whether an exam navigation target URL is permitted.
    /// </summary>
    public NavigationDecision EvaluateExamNavigation(string? targetUri)
    {
        return _allowlistManager.IsExamNavigationAllowed(targetUri);
    }

    /// <summary>
    /// Checks whether a recovery navigation target URL is permitted.
    /// </summary>
    public NavigationDecision EvaluateRecoveryNavigation(string? targetUri)
    {
        return _allowlistManager.IsRecoveryNavigationAllowed(targetUri);
    }
}
