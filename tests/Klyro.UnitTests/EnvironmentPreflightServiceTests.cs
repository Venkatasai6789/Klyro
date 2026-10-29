using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Authentication;
using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Klyro.Core.Preflight;
using Klyro.Core.Security.RemoteAccess;
using Xunit;

namespace Klyro.UnitTests;

public class EnvironmentPreflightServiceTests
{
    private sealed class MockConnectivityProbe : IConnectivityProbe
    {
        public bool InternetSuccess { get; set; } = true;
        public long InternetLatency { get; set; } = 42;
        public bool AssessmentSuccess { get; set; } = true;
        public long AssessmentLatency { get; set; } = 55;

        public Task<ProbeResult> ExecuteAsync(
            ProbeType probeType,
            string endpoint,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            if (probeType == ProbeType.InternetConnectivity)
            {
                if (InternetSuccess)
                {
                    return Task.FromResult(ProbeResult.Success(probeType, endpoint, 204, InternetLatency));
                }
                return Task.FromResult(ProbeResult.Failure(probeType, endpoint, "Internet unreachable", 500, 0));
            }

            if (probeType == ProbeType.AssessmentProvider)
            {
                if (AssessmentSuccess)
                {
                    return Task.FromResult(ProbeResult.Success(probeType, endpoint, 200, AssessmentLatency));
                }
                return Task.FromResult(ProbeResult.Failure(probeType, endpoint, "Host down", 503, 0));
            }

            return Task.FromResult(ProbeResult.Success(probeType, endpoint, 200, 10));
        }
    }

    private sealed class MockSecurityPolicyEvaluator : ISecurityPolicyEvaluator
    {
        public RemoteAccessDetectionResult Result { get; set; } = RemoteAccessDetectionResult.ClearResult();
        public RemoteAccessDetectionResult EvaluateCompliance() => Result;
        public PolicyEnforcementAction DetermineAction(RemoteAccessDetectionResult result, bool isExamActive) =>
            result.IsViolation ? (isExamActive ? PolicyEnforcementAction.RestrictActiveExam : PolicyEnforcementAction.BlockEntry) : PolicyEnforcementAction.Permit;
    }

    [Fact]
    public async Task RunAllChecksAsync_WithValidUserAndProbes_PassesAll()
    {
        var probe = new MockConnectivityProbe();
        var config = new KlyroConfiguration
        {
            Exam = new ExamOptions { StartUrl = "https://example.com/exam" },
            Security = new SecurityOptions { BlockedProcesses = new List<string> { "nonexistent_fake_process_xyz" } }
        };

        var mockSec = new MockSecurityPolicyEvaluator();
        var service = new EnvironmentPreflightService(config, probe, securityEvaluator: mockSec);
        var user = new AuthenticatedUser
        {
            DisplayName = "Test Student",
            Email = "student@university.edu"
        };

        var progressReports = new List<PreflightProgressReport>();
        var progress = new Progress<PreflightProgressReport>(r => progressReports.Add(r));

        bool result = await service.RunAllChecksAsync(user, progress);

        Assert.True(result);
        var items = service.CurrentItems;
        Assert.Equal(5, items.Count);
        Assert.All(items, item => Assert.Equal(PreflightCheckState.Passed, item.State));
        Assert.Contains(items, i => i.Id == PreflightCheckId.Account && i.Subtitle.Contains("University account verified"));
        Assert.Contains(items, i => i.Id == PreflightCheckId.Internet && i.Subtitle.Contains("42ms"));
    }

    [Fact]
    public async Task RunAllChecksAsync_WhenRemoteAccessDetected_FailsSecureEnvironment()
    {
        var probe = new MockConnectivityProbe();
        var mockSec = new MockSecurityPolicyEvaluator
        {
            Result = new RemoteAccessDetectionResult
            {
                Decision = SecurityPolicyDecision.ConfirmedViolation,
                Category = DetectionCategory.WindowsRemoteDesktop,
                Classification = EvidenceClassification.ActiveRemoteSession,
                Details = "Active Microsoft RDP session detected."
            }
        };

        var service = new EnvironmentPreflightService(probe: probe, securityEvaluator: mockSec);
        var user = new AuthenticatedUser { DisplayName = "Student", Email = "student@university.edu" };

        bool result = await service.RunAllChecksAsync(user);

        Assert.False(result);
        var secItem = service.CurrentItems.First(i => i.Id == PreflightCheckId.SecureEnvironment);
        Assert.Equal(PreflightCheckState.Failed, secItem.State);
        Assert.Equal("Remote desktop detected", secItem.Subtitle);
        Assert.NotNull(service.LastSecurityResult);
        Assert.True(service.LastSecurityResult.IsBlocked);
    }

    [Fact]
    public async Task RunAllChecksAsync_WithNullUser_FailsAccountCheck()
    {
        var probe = new MockConnectivityProbe();
        var service = new EnvironmentPreflightService(probe: probe);

        bool result = await service.RunAllChecksAsync(null);

        Assert.False(result);
        var accountItem = service.CurrentItems[0];
        Assert.Equal(PreflightCheckId.Account, accountItem.Id);
        Assert.Equal(PreflightCheckState.Failed, accountItem.State);
        Assert.NotNull(accountItem.DiagnosticDetails);
    }

    [Fact]
    public async Task RunAllChecksAsync_WhenInternetFails_FailsInternetCheck()
    {
        var probe = new MockConnectivityProbe { InternetSuccess = false };
        var service = new EnvironmentPreflightService(probe: probe);
        var user = new AuthenticatedUser { Email = "student@test.com" };

        bool result = await service.RunAllChecksAsync(user);

        Assert.False(result);
        var internetItem = service.CurrentItems[2];
        Assert.Equal(PreflightCheckId.Internet, internetItem.Id);
        Assert.Equal(PreflightCheckState.Failed, internetItem.State);
    }

    [Fact]
    public async Task RunAllChecksAsync_WhenAssessmentHostFails_FailsAssessmentCheck()
    {
        var probe = new MockConnectivityProbe { AssessmentSuccess = false };
        var service = new EnvironmentPreflightService(probe: probe);
        var user = new AuthenticatedUser { Email = "student@test.com" };

        bool result = await service.RunAllChecksAsync(user);

        Assert.False(result);
        var assessmentItem = service.CurrentItems[3];
        Assert.Equal(PreflightCheckId.AssessmentService, assessmentItem.Id);
        Assert.Equal(PreflightCheckState.Failed, assessmentItem.State);
    }

    [Fact]
    public async Task RerunCheckAsync_UpdatesSingleItem()
    {
        var probe = new MockConnectivityProbe { InternetSuccess = false };
        var service = new EnvironmentPreflightService(probe: probe);
        var user = new AuthenticatedUser { Email = "student@test.com" };

        await service.RunAllChecksAsync(user);
        Assert.Equal(PreflightCheckState.Failed, service.CurrentItems[2].State);

        // Fix probe condition and rerun only Internet
        probe.InternetSuccess = true;
        probe.InternetLatency = 30;

        var reran = await service.RerunCheckAsync(PreflightCheckId.Internet, user);

        Assert.Equal(PreflightCheckState.Passed, reran.State);
        Assert.Contains("30ms", reran.Subtitle);
        Assert.Equal(PreflightCheckState.Passed, service.CurrentItems[2].State);
    }

    [Fact]
    public async Task RunAllChecksAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var probe = new MockConnectivityProbe();
        var service = new EnvironmentPreflightService(probe: probe);
        var user = new AuthenticatedUser { Email = "student@test.com" };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.RunAllChecksAsync(user, cancellationToken: cts.Token);
        });
    }

    private sealed class MockSecurityEvaluator : ISecurityPolicyEvaluator
    {
        public RemoteAccessDetectionResult Result { get; set; } = RemoteAccessDetectionResult.ClearResult();

        public RemoteAccessDetectionResult EvaluateCompliance() => Result;

        public PolicyEnforcementAction DetermineAction(RemoteAccessDetectionResult result, bool isExamActive) =>
            result.IsViolation
                ? (isExamActive ? PolicyEnforcementAction.RestrictActiveExam : PolicyEnforcementAction.BlockEntry)
                : PolicyEnforcementAction.Permit;
    }

    [Fact]
    public async Task RunAllChecksAsync_WhenRemoteAccessDetected_FailsSecureEnvironmentCheck()
    {
        var probe = new MockConnectivityProbe();
        var secMock = new MockSecurityEvaluator
        {
            Result = new RemoteAccessDetectionResult
            {
                Decision = SecurityPolicyDecision.ConfirmedViolation,
                Category = DetectionCategory.WindowsRemoteDesktop,
                Details = "Active Windows Remote Desktop session detected."
            }
        };

        var service = new EnvironmentPreflightService(probe: probe, securityEvaluator: secMock);
        var user = new AuthenticatedUser { Email = "student@university.edu" };

        bool passed = await service.RunAllChecksAsync(user);

        Assert.False(passed);
        var secItem = service.CurrentItems[4];
        Assert.Equal(PreflightCheckId.SecureEnvironment, secItem.Id);
        Assert.Equal(PreflightCheckState.Failed, secItem.State);
        Assert.Equal("Remote desktop detected", secItem.Subtitle);
        Assert.NotNull(service.LastSecurityResult);
        Assert.True(service.LastSecurityResult.IsViolation);
    }

    [Fact]
    public async Task RerunCheckAsync_AfterResolvingRemoteAccess_PassesSecureEnvironmentCheck()
    {
        var probe = new MockConnectivityProbe();
        var secMock = new MockSecurityEvaluator
        {
            Result = new RemoteAccessDetectionResult
            {
                Decision = SecurityPolicyDecision.ConfirmedViolation,
                Category = DetectionCategory.RemoteControlSoftware,
                Details = "Active AnyDesk process detected."
            }
        };

        var service = new EnvironmentPreflightService(probe: probe, securityEvaluator: secMock);
        var user = new AuthenticatedUser { Email = "student@university.edu" };

        await service.RunAllChecksAsync(user);
        Assert.Equal(PreflightCheckState.Failed, service.CurrentItems[4].State);

        // Resolve AnyDesk
        secMock.Result = RemoteAccessDetectionResult.ClearResult();
        var reran = await service.RerunCheckAsync(PreflightCheckId.SecureEnvironment, user);

        Assert.Equal(PreflightCheckState.Passed, reran.State);
        Assert.Equal("Environment secure", reran.Subtitle);
    }
}
