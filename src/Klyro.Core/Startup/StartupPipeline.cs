using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Browser;
using Klyro.Core.Configuration;
using Klyro.Core.Health;
using Klyro.Core.Health.Checks;
using Klyro.Core.Logging;
using Klyro.Core.Network;
using Klyro.Core.Persistence;
using Klyro.Core.Security;

namespace Klyro.Core.Startup;

public record StartupProgress(double Percentage, string StatusText, string StageName);

public sealed class StartupResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailedStage { get; init; }
    public KlyroConfiguration Configuration { get; init; } = new();
    public AuditLogger? AuditLogger { get; init; }
    public SessionStore? SessionStore { get; init; }
    public AllowlistManager? AllowlistManager { get; init; }
    public PreflightReport? PreflightReport { get; init; }
}

/// <summary>
/// Orchestrates the actual startup initialization stages for Klyro.
/// Executes genuine environment, system, network, and security policy checks.
/// </summary>
public sealed class StartupPipeline
{
    private readonly string _baseDirectory;

    public StartupPipeline(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    public async Task<StartupResult> RunAsync(IProgress<StartupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        KlyroConfiguration config = new();
        AuditLogger? auditLogger = null;
        SessionStore? sessionStore = null;
        AllowlistManager? allowlistManager = null;
        PreflightReport? report = null;

        try
        {
            // STAGE 1: INITIALIZING (0% - 15%)
            progress?.Report(new StartupProgress(10, "Starting Klyro...", "INITIALIZING"));
            await Task.Delay(250, cancellationToken); // Subtle smooth transition

            config = LoadConfiguration();
            auditLogger = new AuditLogger();
            auditLogger.Log(EventType.EXAM_STARTED, "STARTUP", "Klyro startup pipeline initiated.");

            progress?.Report(new StartupProgress(20, "Preparing secure environment...", "INITIALIZING"));
            await Task.Delay(200, cancellationToken);

            // STAGE 2: ENVIRONMENT VALIDATION (20% - 40%)
            progress?.Report(new StartupProgress(35, "Checking system environment...", "ENVIRONMENT_VALIDATION"));
            var checker = new PreflightChecker(auditLogger);
            checker.RegisterCheck(new WindowsVersionCheck());
            checker.RegisterCheck(new ConfigurationValidationCheck(config));
            checker.RegisterCheck(new DiskStateCheck());
            checker.RegisterCheck(new SystemClockCheck());

            report = await checker.RunAllChecksAsync(cancellationToken);
            var criticalSystemFail = report.Results.FirstOrDefault(r => r.Status == HealthStatus.Fail && r.IsCritical);
            if (criticalSystemFail != null)
            {
                return new StartupResult
                {
                    IsSuccess = false,
                    FailedStage = "ENVIRONMENT_VALIDATION",
                    ErrorMessage = $"System environment check failed: {criticalSystemFail.Message}",
                    Configuration = config,
                    AuditLogger = auditLogger,
                    PreflightReport = report
                };
            }
            await Task.Delay(200, cancellationToken);

            // STAGE 3: NETWORK VALIDATION (40% - 60%)
            progress?.Report(new StartupProgress(50, "Checking network connection...", "NETWORK_VALIDATION"));
            var networkChecker = new PreflightChecker(auditLogger);
            networkChecker.RegisterCheck(new NetworkAdapterCheck());

            var netReport = await networkChecker.RunAllChecksAsync(cancellationToken);
            var criticalNetFail = netReport.Results.FirstOrDefault(r => r.Status == HealthStatus.Fail && r.IsCritical);
            if (criticalNetFail != null)
            {
                return new StartupResult
                {
                    IsSuccess = false,
                    FailedStage = "NETWORK_VALIDATION",
                    ErrorMessage = $"Network adapter check failed: {criticalNetFail.Message}",
                    Configuration = config,
                    AuditLogger = auditLogger,
                    PreflightReport = netReport
                };
            }
            await Task.Delay(200, cancellationToken);

            // STAGE 4: SECURITY SERVICES (60% - 80%)
            progress?.Report(new StartupProgress(70, "Loading security services...", "SECURITY_SERVICES"));
            allowlistManager = new AllowlistManager(config);
            var navGuard = new NavigationGuard(allowlistManager);
            var frameGuard = new FrameNavigationGuard(allowlistManager);
            var popupGuard = new PopupGuard(allowlistManager, config.Security);
            var downloadGuard = new DownloadGuard(allowlistManager, config.Security);
            var protocolGuard = new ProtocolGuard();
            await Task.Delay(200, cancellationToken);

            // STAGE 5: SESSION PREPARATION (80% - 95%)
            progress?.Report(new StartupProgress(85, "Preparing secure session...", "SESSION_PREPARATION"));
            sessionStore = new SessionStore();
            var existingSession = await sessionStore.LoadActiveSessionAsync();
            await Task.Delay(200, cancellationToken);

            // STAGE 6: READY (95% - 100%)
            progress?.Report(new StartupProgress(100, "Ready.", "READY"));
            await Task.Delay(300, cancellationToken);

            return new StartupResult
            {
                IsSuccess = true,
                Configuration = config,
                AuditLogger = auditLogger,
                SessionStore = sessionStore,
                AllowlistManager = allowlistManager,
                PreflightReport = report
            };
        }
        catch (OperationCanceledException)
        {
            return new StartupResult
            {
                IsSuccess = false,
                FailedStage = "CANCELED",
                ErrorMessage = "Startup process was canceled."
            };
        }
        catch (Exception ex)
        {
            auditLogger?.Log(EventType.KLYRO_CRASH, "STARTUP_EXCEPTION", ex.Message);
            return new StartupResult
            {
                IsSuccess = false,
                FailedStage = "STARTUP_ERROR",
                ErrorMessage = $"Fatal startup error: {ex.Message}",
                Configuration = config,
                AuditLogger = auditLogger
            };
        }
    }

    private KlyroConfiguration LoadConfiguration()
    {
        string[] candidates = { "config/exam.json", "config/default.json", "config/development.json" };
        foreach (var rel in candidates)
        {
            try
            {
                var full = Path.Combine(_baseDirectory, rel);
                if (File.Exists(full))
                {
                    return ConfigurationLoader.LoadFromFile(full);
                }
                if (File.Exists(rel))
                {
                    return ConfigurationLoader.LoadFromFile(rel);
                }
            }
            catch { }
        }
        return new KlyroConfiguration();
    }
}
