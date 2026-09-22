using System;
using System.Collections.Generic;
using System.Linq;
using Klyro.Core.Configuration;

namespace Klyro.Core.Security;

/// <summary>
/// Enforces origin allowlist verification with lookalike and spoofing defenses.
/// </summary>
public sealed class AllowlistManager
{
    private readonly HashSet<UriOrigin> _examAllowedOrigins = new();
    private readonly HashSet<UriOrigin> _recoveryAllowedOrigins = new();
    private readonly HashSet<UriOrigin> _approvedDownloadOrigins = new();
    private readonly bool _strictHttps;

    public AllowlistManager(KlyroConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _strictHttps = !string.Equals(config.Application.Environment, "Development", StringComparison.OrdinalIgnoreCase);

        PopulateOrigins(config.Browser.AllowedOrigins, _examAllowedOrigins);
        PopulateOrigins(config.Browser.RecoveryAllowedOrigins, _recoveryAllowedOrigins);
        PopulateOrigins(config.Browser.ApprovedDownloadOrigins, _approvedDownloadOrigins);
    }

    private static void PopulateOrigins(IEnumerable<string> source, HashSet<UriOrigin> target)
    {
        foreach (var entry in source)
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var uri))
            {
                target.Add(new UriOrigin(uri));
            }
        }
    }

    /// <summary>
    /// Evaluates if an exam navigation request is allowed.
    /// </summary>
    public NavigationDecision IsExamNavigationAllowed(string? url)
    {
        return EvaluateUrl(url, _examAllowedOrigins, "Exam");
    }

    /// <summary>
    /// Evaluates if a recovery navigation request is allowed.
    /// </summary>
    public NavigationDecision IsRecoveryNavigationAllowed(string? url)
    {
        return EvaluateUrl(url, _recoveryAllowedOrigins, "Recovery");
    }

    /// <summary>
    /// Evaluates if a download request is allowed.
    /// </summary>
    public NavigationDecision IsDownloadAllowed(string? url)
    {
        return EvaluateUrl(url, _approvedDownloadOrigins, "Download");
    }

    private NavigationDecision EvaluateUrl(string? url, HashSet<UriOrigin> allowedOrigins, string context)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return NavigationDecision.Block(url, $"{context} navigation URL is empty.");
        }

        // Handle about:blank (required for initial WebView load)
        if (string.Equals(url.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDecision.Allow(url, "Internal browser initialization page");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
        {
            return NavigationDecision.Block(url, $"Malformed absolute URI in {context} navigation: '{url}'");
        }

        // Check protocol policy first
        var protocolDecision = ProtocolPolicy.EvaluateProtocol(targetUri);
        if (!protocolDecision.IsAllowed)
        {
            return protocolDecision;
        }

        // Enforce HTTPS in production for exam navigations
        if (_strictHttps && context == "Exam" && !string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDecision.Block(url, $"Insecure HTTP scheme is prohibited in production exam environment.");
        }

        var targetOrigin = new UriOrigin(targetUri);

        // Strict Origin Matching: Scheme, Host, and Port must match exactly
        bool matched = allowedOrigins.Contains(targetOrigin);

        if (!matched && context == "Exam" && string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // When Google OAuth is allowlisted, permit legitimate Google Identity & 2SV endpoints
            bool hasGoogleAuth = allowedOrigins.Any(o => o.Host.Contains("accounts.google.", StringComparison.OrdinalIgnoreCase));
            if (hasGoogleAuth && IsGoogleAuthDomain(targetUri.Host))
            {
                matched = true;
            }
        }

        if (matched)
        {
            return NavigationDecision.Allow(url, $"Origin '{targetOrigin}' matched {context} allowlist.");
        }

        // Check for common phishing / lookalike domain attacks to provide precise audit logs
        string threatDetail = DetectLookalikeThreat(targetUri.Host, allowedOrigins);
        return NavigationDecision.Block(url, $"Origin '{targetOrigin}' is not permitted in {context} allowlist.{threatDetail}");
    }

    private static bool IsGoogleAuthDomain(string host)
    {
        var lower = host.ToLowerInvariant();
        return lower.StartsWith("accounts.google.") ||
               lower.StartsWith("myaccount.google.") ||
               lower.Equals("oauth2.googleapis.com", StringComparison.Ordinal) ||
               lower.Equals("ssl.gstatic.com", StringComparison.Ordinal) ||
               lower.Equals("www.gstatic.com", StringComparison.Ordinal) ||
               lower.Equals("fonts.gstatic.com", StringComparison.Ordinal) ||
               lower.Equals("fonts.googleapis.com", StringComparison.Ordinal) ||
               lower.Equals("apis.google.com", StringComparison.Ordinal) ||
               lower.Equals("content.googleapis.com", StringComparison.Ordinal) ||
               lower.Equals("accounts.youtube.com", StringComparison.Ordinal);
    }

    private static string DetectLookalikeThreat(string host, HashSet<UriOrigin> allowedOrigins)
    {
        foreach (var allowed in allowedOrigins)
        {
            var allowedHost = allowed.Host;
            var baseDomain = allowedHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? allowedHost[4..]
                : allowedHost;

            if (host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
            {
                return $" Note: '{host}' is an unapproved subdomain of '{baseDomain}'. Explicit subdomain whitelisting is required.";
            }

            if (host.Contains(baseDomain, StringComparison.OrdinalIgnoreCase) && !host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase))
            {
                return $" Security Alert: '{host}' appears to be a spoofed or lookalike domain of '{baseDomain}'.";
            }
        }

        return string.Empty;
    }

    private readonly struct UriOrigin : IEquatable<UriOrigin>
    {
        public string Scheme { get; }
        public string Host { get; }
        public int Port { get; }

        public UriOrigin(Uri uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant();
            Host = uri.Host.ToLowerInvariant();
            Port = uri.Port;
        }

        public bool Equals(UriOrigin other)
        {
            return string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
                   string.Equals(Host, other.Host, StringComparison.Ordinal) &&
                   Port == other.Port;
        }

        public override bool Equals(object? obj) => obj is UriOrigin other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port);

        public override string ToString() => $"{Scheme}://{Host}:{Port}";
    }
}
