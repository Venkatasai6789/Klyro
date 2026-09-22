using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Health;
using Xunit;

namespace Klyro.UnitTests;

public class PreflightCheckerTests
{
    private sealed class MockCheck : IPreflightCheck
    {
        public string Name { get; }
        public bool IsCritical { get; }
        private readonly HealthStatus _status;
        private readonly string _message;

        public MockCheck(string name, bool isCritical, HealthStatus status, string message)
        {
            Name = name;
            IsCritical = isCritical;
            _status = status;
            _message = message;
        }

        public Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default)
        {
            var result = _status switch
            {
                HealthStatus.Pass => HealthCheckResult.Pass(Name, _message),
                HealthStatus.Warning => HealthCheckResult.Warning(Name, _message),
                HealthStatus.Fail => HealthCheckResult.Fail(Name, _message, IsCritical),
                _ => HealthCheckResult.Pass(Name, _message)
            };
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task AllPass_CanStartExamIsTrue()
    {
        var checker = new PreflightChecker();
        checker.RegisterCheck(new MockCheck("OS", true, HealthStatus.Pass, "Windows 11"));
        checker.RegisterCheck(new MockCheck("Network", true, HealthStatus.Pass, "Connected"));

        var report = await checker.RunAllChecksAsync();

        Assert.True(report.CanStartExam);
        Assert.False(report.HasWarnings);
        Assert.Equal(2, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
    }

    [Fact]
    public async Task CriticalFailure_CanStartExamIsFalse()
    {
        var checker = new PreflightChecker();
        checker.RegisterCheck(new MockCheck("OS", true, HealthStatus.Pass, "Windows 11"));
        checker.RegisterCheck(new MockCheck("Security", true, HealthStatus.Fail, "App control missing"));

        var report = await checker.RunAllChecksAsync();

        Assert.False(report.CanStartExam);
        Assert.Equal(1, report.PassedCount);
        Assert.Equal(1, report.FailedCount);
    }

    [Fact]
    public async Task NonCriticalWarning_CanStartExamIsTrue_HasWarningsIsTrue()
    {
        var checker = new PreflightChecker();
        checker.RegisterCheck(new MockCheck("OS", true, HealthStatus.Pass, "Windows 11"));
        checker.RegisterCheck(new MockCheck("Disk", false, HealthStatus.Warning, "Disk space low"));

        var report = await checker.RunAllChecksAsync();

        Assert.True(report.CanStartExam); // Non-critical warning does NOT block exam!
        Assert.True(report.HasWarnings);
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public async Task CreateDefault_RunsStandardChecks()
    {
        var config = new KlyroConfiguration();
        var checker = PreflightChecker.CreateDefault(config);

        var report = await checker.RunAllChecksAsync();

        Assert.NotEmpty(report.Results);
        Assert.Contains(report.Results, r => r.CheckName == "Windows Version");
        Assert.Contains(report.Results, r => r.CheckName == "Configuration Integrity");
    }
}
