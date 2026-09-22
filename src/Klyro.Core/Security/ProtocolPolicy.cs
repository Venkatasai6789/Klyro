using System;
using System.Collections.Generic;

namespace Klyro.Core.Security;

/// <summary>
/// Enforces external and dangerous protocol blocking rules.
/// </summary>
public sealed class ProtocolPolicy
{
    private static readonly HashSet<string> DangerousSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file",
        "mailto",
        "tel",
        "sms",
        "ms-settings",
        "ms-windows-store",
        "powershell",
        "cmd",
        "shell",
        "javascript",
        "data",
        "vbscript",
        "about", // except about:blank
        "chrome",
        "edge",
        "view-source",
        "res",
        "ws",
        "wss" // WebSockets to external origins are gated via network policy
    };

    private static readonly HashSet<string> StandardWebSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https",
        "http"
    };

    /// <summary>
    /// Evaluates whether a protocol scheme is permitted.
    /// </summary>
    public static NavigationDecision EvaluateProtocol(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var scheme = uri.Scheme;

        // Allow benign about:blank (used during WebView2 initialization)
        if (string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDecision.Allow(uri.AbsoluteUri, "Internal browser blank document");
        }

        if (DangerousSchemes.Contains(scheme))
        {
            return NavigationDecision.Block(uri.AbsoluteUri, $"Dangerous or external protocol '{scheme}:' is blocked by security policy.");
        }

        if (!StandardWebSchemes.Contains(scheme))
        {
            return NavigationDecision.Block(uri.AbsoluteUri, $"Unrecognized protocol scheme '{scheme}:' is not permitted.");
        }

        return NavigationDecision.Allow(uri.AbsoluteUri, "Protocol scheme is valid web scheme.");
    }
}
