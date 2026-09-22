using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Network;

namespace Klyro.Core.Health.Checks;

public sealed class WindowsVersionCheck : IPreflightCheck
{
    public string Name => "Windows Version";
    public bool IsCritical => true;

    public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Task.FromResult(HealthCheckResult.Fail(Name, "Operating System is not Windows.", IsCritical, "Klyro requires Windows 10/11."));
        }

        var osVersion = Environment.OSVersion.Version;
        if (osVersion.Major >= 10)
        {
            return Task.FromResult(HealthCheckResult.Pass(Name, $"Windows {osVersion.Major}.{osVersion.Minor} Build {osVersion.Build} verified."));
        }

        return Task.FromResult(HealthCheckResult.Fail(Name, $"Unsupported Windows version: {osVersion}", IsCritical, "Upgrade to Windows 10 or 11."));
    }
}

public sealed class ConfigurationValidationCheck : IPreflightCheck
{
    private readonly KlyroConfiguration _config;
    public string Name => "Configuration Integrity";
    public bool IsCritical => true;

    public ConfigurationValidationCheck(KlyroConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var validation = ConfigurationValidator.Validate(_config);
        if (validation.IsValid)
        {
            return Task.FromResult(HealthCheckResult.Pass(Name, "Configuration verified with zero errors."));
        }

        var errors = string.Join("; ", validation.Errors);
        return Task.FromResult(HealthCheckResult.Fail(Name, $"Configuration errors: {errors}", IsCritical, "Review configuration file."));
    }
}

public sealed class NetworkAdapterCheck : IPreflightCheck
{
    public string Name => "Network Adapter";
    public bool IsCritical => true;

    public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        if (activeInterface != null)
        {
            return Task.FromResult(HealthCheckResult.Pass(Name, $"Active adapter detected: {activeInterface.Name} ({activeInterface.NetworkInterfaceType})"));
        }

        return Task.FromResult(HealthCheckResult.Fail(Name, "No operational network adapter found.", IsCritical, "Verify Wi-Fi or Ethernet cable is connected."));
    }
}

public sealed class DiskStateCheck : IPreflightCheck
{
    public string Name => "Disk Storage State";
    public bool IsCritical => false;

    public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var driveInfo = new DriveInfo(systemDrive);

            // Require at least 200 MB free for WebView2 profile and cache
            long freeMb = driveInfo.AvailableFreeSpace / (1024 * 1024);
            if (freeMb >= 200)
            {
                return Task.FromResult(HealthCheckResult.Pass(Name, $"Sufficient disk space available: {freeMb} MB free on {systemDrive}"));
            }

            return Task.FromResult(HealthCheckResult.Warning(Name, $"Low disk space: only {freeMb} MB free on {systemDrive}", "Free up disk space for optimal browser caching."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Warning(Name, $"Could not query drive space: {ex.Message}"));
        }
    }
}

public sealed class SystemClockCheck : IPreflightCheck
{
    public string Name => "System Clock Synchronization";
    public bool IsCritical => false;

    public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var localTime = DateTimeOffset.Now;
        // Verify clock is not set to unreasonable epoch (e.g. before 2024)
        if (localTime.Year >= 2024)
        {
            return Task.FromResult(HealthCheckResult.Pass(Name, $"System clock verified: {localTime:yyyy-MM-dd HH:mm:ss zzz}"));
        }

        return Task.FromResult(HealthCheckResult.Warning(Name, $"System clock may be inaccurate: {localTime}", "Ensure workstation clock is synchronized via NTP."));
    }
}

public sealed class AssessmentProviderCheck : IPreflightCheck
{
    private readonly IConnectivityProbe _probe;
    private readonly string _startUrl;
    public string Name => "Assessment Provider Reachability";
    public bool IsCritical => true;

    public AssessmentProviderCheck(IConnectivityProbe probe, string startUrl)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _startUrl = startUrl ?? throw new ArgumentNullException(nameof(startUrl));
    }

    public async Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = await _probe.ExecuteAsync(ProbeType.AssessmentProvider, _startUrl, 3000, cancellationToken);
        if (result.IsSuccess)
        {
            return HealthCheckResult.Pass(Name, $"Assessment service reachable ({result.LatencyMs}ms)");
        }

        return HealthCheckResult.Fail(Name, $"Assessment service unreachable at {_startUrl}: {result.ErrorMessage}", IsCritical, "Verify Internet connection or assessment contest schedule.");
    }
}
