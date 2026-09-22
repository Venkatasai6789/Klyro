using System;
using Klyro.Core.Configuration;
using Klyro.Core.Security;

namespace Klyro.Core.Browser;

/// <summary>
/// Controls and intercepts file download requests.
/// </summary>
public sealed class DownloadGuard
{
    private readonly AllowlistManager _allowlistManager;
    private readonly bool _allowDownloads;

    public DownloadGuard(AllowlistManager allowlistManager, SecurityOptions securityOptions)
    {
        _allowlistManager = allowlistManager ?? throw new ArgumentNullException(nameof(allowlistManager));
        ArgumentNullException.ThrowIfNull(securityOptions);

        _allowDownloads = securityOptions.AllowDownloads;
    }

    /// <summary>
    /// Evaluates whether a file download is permitted.
    /// Default policy: FAIL CLOSED (BLOCK).
    /// </summary>
    public NavigationDecision EvaluateDownloadRequest(string? downloadUri)
    {
        if (!_allowDownloads)
        {
            return NavigationDecision.Block(downloadUri, "File downloads are prohibited by examination security policy.");
        }

        var decision = _allowlistManager.IsDownloadAllowed(downloadUri);
        if (!decision.IsAllowed)
        {
            return NavigationDecision.Block(downloadUri, $"Download blocked: {decision.Reason}");
        }

        return NavigationDecision.Allow(downloadUri, "Download approved for designated assessment resource.");
    }
}
