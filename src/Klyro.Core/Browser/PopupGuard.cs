using System;
using Klyro.Core.Configuration;
using Klyro.Core.Security;

namespace Klyro.Core.Browser;

/// <summary>
/// Suppresses and regulates new window and popup requests.
/// </summary>
public sealed class PopupGuard
{
    private readonly AllowlistManager _allowlistManager;
    private readonly bool _allowNewWindows;
    private readonly bool _allowNewTabs;

    public PopupGuard(AllowlistManager allowlistManager, SecurityOptions securityOptions)
    {
        _allowlistManager = allowlistManager ?? throw new ArgumentNullException(nameof(allowlistManager));
        ArgumentNullException.ThrowIfNull(securityOptions);

        _allowNewWindows = securityOptions.AllowNewWindows;
        _allowNewTabs = securityOptions.AllowNewTabs;
    }

    /// <summary>
    /// Evaluates whether a new window or popup request should be allowed.
    /// Default policy: FAIL CLOSED (BLOCK).
    /// </summary>
    public NavigationDecision EvaluateNewWindowRequest(string? targetUri)
    {
        if (!_allowNewWindows && !_allowNewTabs)
        {
            return NavigationDecision.Block(targetUri, "New windows, popups, and tabs are disabled by examination security policy.");
        }

        // Even if enabled by configuration, origin must be strictly allowlisted
        var allowlistDecision = _allowlistManager.IsExamNavigationAllowed(targetUri);
        if (!allowlistDecision.IsAllowed)
        {
            return NavigationDecision.Block(targetUri, $"Popup request blocked: {allowlistDecision.Reason}");
        }

        return NavigationDecision.Allow(targetUri, "Popup request approved for allowlisted origin.");
    }
}
