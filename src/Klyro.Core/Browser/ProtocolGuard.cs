using System;
using Klyro.Core.Security;

namespace Klyro.Core.Browser;

/// <summary>
/// Intercepts and denies external and OS protocol handler invocations.
/// </summary>
public sealed class ProtocolGuard
{
    /// <summary>
    /// Evaluates whether an external protocol URI is permitted.
    /// Blocks mailto:, tel:, file:, ms-settings:, etc.
    /// </summary>
    public NavigationDecision EvaluateProtocol(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return NavigationDecision.Block(url, "Empty URL specified for protocol evaluation.");
        }

        if (string.Equals(url.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDecision.Allow(url, "Internal browser blank document");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return NavigationDecision.Block(url, $"Malformed URI format: '{url}'");
        }

        return ProtocolPolicy.EvaluateProtocol(uri);
    }
}
