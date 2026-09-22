using System;

namespace Klyro.Core.Network;

public enum ProbeType
{
    Gateway,
    CaptivePortal,
    InternetConnectivity,
    AssessmentProvider
}

/// <summary>
/// Result of an individual connectivity probe.
/// </summary>
public sealed class ProbeResult
{
    public ProbeType Type { get; init; }
    public string TargetEndpoint { get; init; } = string.Empty;
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ErrorMessage { get; init; }

    public static ProbeResult Success(ProbeType type, string endpoint, int statusCode, long latencyMs) =>
        new()
        {
            Type = type,
            TargetEndpoint = endpoint,
            IsSuccess = true,
            StatusCode = statusCode,
            LatencyMs = latencyMs
        };

    public static ProbeResult Failure(ProbeType type, string endpoint, string errorMessage, int statusCode = 0, long latencyMs = 0) =>
        new()
        {
            Type = type,
            TargetEndpoint = endpoint,
            IsSuccess = false,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            ErrorMessage = errorMessage
        };
}
