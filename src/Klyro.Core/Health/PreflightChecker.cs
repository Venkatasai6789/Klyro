using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Health.Checks;
using Klyro.Core.Logging;
using Klyro.Core.Network;

namespace Klyro.Core.Health;

/// <summary>
/// Aggregated report from a full pre-flight health evaluation.
/// </summary>
public sealed class PreflightReport
{
    public List<HealthCheckResult> Results { get; } = new();
    public bool CanStartExam => Results.All(r => !(r.Status == HealthStatus.Fail && r.IsCritical));
    public bool HasWarnings => Results.Any(r => r.Status == HealthStatus.Warning);
    public int PassedCount => Results.Count(r => r.Status == HealthStatus.Pass);
    public int FailedCount => Results.Count(r => r.Status == HealthStatus.Fail);
    public int WarningCount => Results.Count(r => r.Status == HealthStatus.Warning);
}

/// <summary>
/// Parallel pre-flight environmental verification engine.
/// Runs comprehensive system, network, and security policy checks concurrently across CPU threads
/// prior to exam launch, slashing preflight latency.
/// </summary>
public sealed class PreflightChecker
{
    private readonly List<IPreflightCheck> _checks = new();
    private readonly IAuditLogger? _auditLogger;

    public PreflightChecker(IAuditLogger? auditLogger = null)
    {
        _auditLogger = auditLogger;
    }

    public void RegisterCheck(IPreflightCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add(check);
    }

    /// <summary>
    /// Populates standard checks based on configuration and network probe.
    /// </summary>
    public static PreflightChecker CreateDefault(KlyroConfiguration config, IConnectivityProbe? probe = null, IAuditLogger? auditLogger = null)
    {
        var checker = new PreflightChecker(auditLogger);
        checker.RegisterCheck(new WindowsVersionCheck());
        checker.RegisterCheck(new ConfigurationValidationCheck(config));
        checker.RegisterCheck(new NetworkAdapterCheck());
        checker.RegisterCheck(new DiskStateCheck());
        checker.RegisterCheck(new SystemClockCheck());

        if (probe != null)
        {
            checker.RegisterCheck(new AssessmentProviderCheck(probe, config.Exam.StartUrl));
        }

        return checker;
    }

    /// <summary>
    /// Executes all registered preflight diagnostics concurrently in parallel tasks across threads.
    /// </summary>
    public async Task<PreflightReport> RunAllChecksAsync(CancellationToken cancellationToken = default)
    {
        var report = new PreflightReport();

        var tasks = _checks.Select(async check =>
        {
            try
            {
                var result = await check.RunAsync(cancellationToken);
                if (result.Status == HealthStatus.Fail && result.IsCritical)
                {
                    _auditLogger?.Log(
                        EventType.SECURITY_VIOLATION,
                        "PreflightFailed",
                        $"Critical check '{check.Name}' failed: {result.Message}");
                }
                return result;
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Fail(check.Name, $"Check threw exception: {ex.Message}", check.IsCritical);
            }
        });

        var results = await Task.WhenAll(tasks);
        report.Results.AddRange(results);

        return report;
    }
}
