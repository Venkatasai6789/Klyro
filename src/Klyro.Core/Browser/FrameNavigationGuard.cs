using System;
using Klyro.Core.Security;

namespace Klyro.Core.Browser;

/// <summary>
/// Evaluates child iframe and sub-frame navigations to prevent clickjacking and unapproved embedding.
/// </summary>
public sealed class FrameNavigationGuard
{
    private readonly AllowlistManager _allowlistManager;

    public FrameNavigationGuard(AllowlistManager allowlistManager)
    {
        _allowlistManager = allowlistManager ?? throw new ArgumentNullException(nameof(allowlistManager));
    }

    /// <summary>
    /// Checks whether an iframe navigation is permitted.
    /// </summary>
    public NavigationDecision EvaluateFrameNavigation(string? frameUri, bool isRecoveryContext = false)
    {
        return isRecoveryContext
            ? _allowlistManager.IsRecoveryNavigationAllowed(frameUri)
            : _allowlistManager.IsExamNavigationAllowed(frameUri);
    }
}
