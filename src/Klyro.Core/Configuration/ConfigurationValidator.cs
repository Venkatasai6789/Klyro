using System;
using System.Collections.Generic;
using System.Linq;

namespace Klyro.Core.Configuration;

/// <summary>
/// Result of configuration validation.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public void AddError(string error) => Errors.Add(error);
    public void AddWarning(string warning) => Warnings.Add(warning);
}

/// <summary>
/// Enforces fail-closed validation on Klyro configuration files.
/// </summary>
public static class ConfigurationValidator
{
    private static readonly string[] DangerousWildcards = { "*", "http://*", "https://*", "*://*", "http://*/*", "https://*/*" };

    public static ValidationResult Validate(KlyroConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new ValidationResult();

        // 1. Validate Exam Start URL
        if (string.IsNullOrWhiteSpace(config.Exam.StartUrl))
        {
            result.AddError("Exam.StartUrl cannot be empty.");
        }
        else if (!Uri.TryCreate(config.Exam.StartUrl, UriKind.Absolute, out var startUri))
        {
            result.AddError($"Exam.StartUrl is not a valid absolute URI: '{config.Exam.StartUrl}'");
        }
        else
        {
            if (!string.Equals(startUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(config.Application.Environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError($"Exam.StartUrl must use HTTPS in production environments. Provided: '{startUri.Scheme}'");
            }
        }

        // 2. Validate Portal URL
        if (string.IsNullOrWhiteSpace(config.Network.PortalUrl))
        {
            result.AddError("Network.PortalUrl cannot be empty.");
        }
        else if (!Uri.TryCreate(config.Network.PortalUrl, UriKind.Absolute, out var portalUri))
        {
            result.AddError($"Network.PortalUrl is not a valid absolute URI: '{config.Network.PortalUrl}'");
        }
        else if (portalUri.Port <= 0 || portalUri.Port > 65535)
        {
            result.AddError($"Network.PortalUrl has an invalid port number: {portalUri.Port}");
        }

        // 3. Validate Timer Policy
        var validPolicies = new[] { "PauseDuringVerifiedNetworkFailure", "ContinueDuringNetworkFailure" };
        if (!validPolicies.Contains(config.Exam.TimerPolicy, StringComparer.OrdinalIgnoreCase))
        {
            result.AddError($"Invalid Exam.TimerPolicy '{config.Exam.TimerPolicy}'. Must be one of: {string.Join(", ", validPolicies)}");
        }

        // 4. Validate Network Timeouts & Thresholds
        if (config.Network.ProbeIntervalMs < 500 || config.Network.ProbeIntervalMs > 30000)
        {
            result.AddError($"Network.ProbeIntervalMs ({config.Network.ProbeIntervalMs}ms) must be between 500ms and 30000ms.");
        }

        if (config.Network.ProbeTimeoutMs < 200 || config.Network.ProbeTimeoutMs >= config.Network.ProbeIntervalMs)
        {
            result.AddError($"Network.ProbeTimeoutMs ({config.Network.ProbeTimeoutMs}ms) must be >= 200ms and less than ProbeIntervalMs ({config.Network.ProbeIntervalMs}ms).");
        }

        if (config.Network.FailureThreshold < 1 || config.Network.FailureThreshold > 10)
        {
            result.AddError($"Network.FailureThreshold ({config.Network.FailureThreshold}) must be between 1 and 10.");
        }

        if (config.Network.RecoveryTimeoutMs < 5000)
        {
            result.AddError($"Network.RecoveryTimeoutMs ({config.Network.RecoveryTimeoutMs}ms) must be at least 5000ms.");
        }

        // 5. Validate Allowed Origins (Fail-closed on dangerous patterns)
        ValidateOriginsList(config.Browser.AllowedOrigins, "Browser.AllowedOrigins", result);
        ValidateOriginsList(config.Browser.RecoveryAllowedOrigins, "Browser.RecoveryAllowedOrigins", result);

        // In production, DevTools must not be enabled
        if (string.Equals(config.Application.Environment, "Production", StringComparison.OrdinalIgnoreCase) && config.Security.AllowDevTools)
        {
            result.AddError("Security.AllowDevTools cannot be enabled in a Production environment.");
        }

        return result;
    }

    private static void ValidateOriginsList(List<string> origins, string fieldName, ValidationResult result)
    {
        if (origins == null || origins.Count == 0)
        {
            result.AddError($"{fieldName} cannot be empty. At least one origin must be specified.");
            return;
        }

        var uniqueOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var origin in origins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                result.AddError($"{fieldName} contains an empty or whitespace origin entry.");
                continue;
            }

            var trimmed = origin.Trim();

            // Check for wildcards
            if (DangerousWildcards.Any(w => trimmed.Equals(w, StringComparison.OrdinalIgnoreCase) || trimmed.Contains('*')))
            {
                result.AddError($"{fieldName} contains dangerous wildcard pattern '{trimmed}'. Wildcard domains are strictly prohibited in Klyro.");
                continue;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                result.AddError($"{fieldName} entry '{trimmed}' is not a valid absolute URI.");
                continue;
            }

            if (!uniqueOrigins.Add(trimmed))
            {
                result.AddWarning($"{fieldName} contains duplicate origin entry '{trimmed}'.");
            }
        }
    }
}
