namespace Klyro.Core.Health;

public enum HealthStatus
{
    Pass,
    Warning,
    Fail
}

/// <summary>
/// Individual result of a pre-flight or environmental health check.
/// </summary>
public sealed class HealthCheckResult
{
    public string CheckName { get; init; } = string.Empty;
    public HealthStatus Status { get; init; }
    public bool IsCritical { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Remediation { get; init; }

    public static HealthCheckResult Pass(string checkName, string message) =>
        new() { CheckName = checkName, Status = HealthStatus.Pass, IsCritical = false, Message = message };

    public static HealthCheckResult Warning(string checkName, string message, string? remediation = null) =>
        new() { CheckName = checkName, Status = HealthStatus.Warning, IsCritical = false, Message = message, Remediation = remediation };

    public static HealthCheckResult Fail(string checkName, string message, bool isCritical = true, string? remediation = null) =>
        new() { CheckName = checkName, Status = HealthStatus.Fail, IsCritical = isCritical, Message = message, Remediation = remediation };
}
