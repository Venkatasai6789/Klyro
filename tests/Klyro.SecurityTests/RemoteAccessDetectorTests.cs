using System;
using System.Collections.Generic;
using Klyro.Core.Configuration;
using Klyro.Core.Security;
using Klyro.Core.Security.RemoteAccess;
using Xunit;

namespace Klyro.SecurityTests;

public class RemoteAccessDetectorTests
{
    private sealed class MockSessionQueryProvider : ISessionQueryProvider
    {
        public bool IsRemoteSession { get; set; }
        public bool IsRemoteControl { get; set; }
        public WtsSessionInfo SessionInfo { get; set; } = new() { QuerySucceeded = true, WinStationName = "Console" };

        public bool IsRemoteSessionMetricSet() => IsRemoteSession;
        public bool IsRemoteControlMetricSet() => IsRemoteControl;
        public WtsSessionInfo GetCurrentWtsSessionInfo() => SessionInfo;
    }

    private sealed class MockProcessProvider : IProcessProvider
    {
        public int CurrentSessionId { get; set; } = 1;
        public List<RunningProcessInfo> Processes { get; set; } = new();
        public IReadOnlyList<RunningProcessInfo> GetRunningProcesses() => Processes;
    }

    private sealed class MockWindowQueryProvider : IWindowQueryProvider
    {
        public List<WindowInfo> Windows { get; set; } = new();
        public IReadOnlyList<WindowInfo> GetTopLevelWindows() => Windows;
    }

    [Fact]
    public void RemoteSessionDetector_WhenConsoleAndNoMetric_ReturnsClear()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            IsRemoteControl = false,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "Console",
                ClientName = "",
                ProtocolType = 0
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public void RemoteSessionDetector_WhenMetricRemoteSessionSet_ReturnsConfirmedViolation()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = true,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "RDP-Tcp#1",
                ClientName = "DESKTOP-REMOTE",
                ProtocolType = 2
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.WindowsRemoteDesktop, result.Category);
        Assert.True(result.IsViolation);
        Assert.Contains("Microsoft RDP", result.Details);
        Assert.Contains("DESKTOP-REMOTE", result.Details);
    }

    [Fact]
    public void RemoteSessionDetector_WhenConsoleStationWithStaleRdpMetric_ReturnsClear()
    {
        // Bug reproduction: RDP disconnected, candidate sits at physical console.
        // Windows retains SM_REMOTESESSION = 1, ProtocolType = 2, and ClientName,
        // but WinStationName is "Console" and no remote shadowing is occurring.
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = true, // Stale metric from disconnected RDP
            IsRemoteControl = false,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "Console", // Physical display adapter
                ClientName = "DESKTOP-PRIOR-RDP", // Stale client name
                ProtocolType = 2, // Stale protocol
                ConnectState = 0 // WTSActive
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.False(result.IsViolation);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public void RemoteSessionDetector_WhenConsoleStationShadowed_ReturnsConfirmedViolation()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            IsRemoteControl = true, // Remotely shadowed via Terminal Services
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "Console",
                ConnectState = 0
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.True(result.IsViolation);
        Assert.Contains("Active remote control or session shadowing", result.Details);
    }

    [Fact]
    public void RemoteSessionDetector_WhenDisconnectedSessionAndNotAllowed_ReturnsViolation()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = true,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "RDP-Tcp#2",
                ClientName = "DESKTOP-CLIENT",
                ProtocolType = 2,
                ConnectState = 4 // WTSDisconnected
            }
        };

        var detector = new RemoteSessionDetector(mock, allowDisconnectedSessions: false);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(EvidenceClassification.DisconnectedSession, result.Classification);
        Assert.Contains("disconnected remote desktop session", result.Details);
    }

    [Fact]
    public void RemoteSessionDetector_WhenDisconnectedSessionAndAllowed_ReturnsClear()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = true,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "RDP-Tcp#2",
                ClientName = "DESKTOP-CLIENT",
                ProtocolType = 2,
                ConnectState = 4 // WTSDisconnected
            }
        };

        var detector = new RemoteSessionDetector(mock, allowDisconnectedSessions: true);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public void RemoteSessionDetector_WhenWtsQueryThrows_ReturnsMonitorUnavailable()
    {
        var mock = new MockSessionQueryProvider
        {
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = false,
                ErrorMessage = "Access Denied 0x5"
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.MonitorUnavailable, result.Decision);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenAnyDeskRunning_ReturnsViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(101, "explorer", "Windows Explorer"),
                new(202, "anydesk", "AnyDesk")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.RemoteControlSoftware, result.Category);
        Assert.Contains("anydesk", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenAccessibilityAppsRunning_ExemptsAndReturnsClear()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(101, "explorer", "Windows Explorer"),
                new(202, "narrator", "Screen Reader"),
                new(303, "nvda", "NVDA Access"),
                new(404, "osk", "On-Screen Keyboard")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenObsRunning_ReturnsScreenSharingViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(101, "obs64", "OBS Studio")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.ScreenSharing, result.Category);
    }

    [Fact]
    public void SecurityPolicyEvaluator_SynthesizesCombinedSignals()
    {
        var sessionMock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "Console" }
        };

        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(500, "teamviewer", "TeamViewer")
            }
        };

        var sessionDetector = new RemoteSessionDetector(sessionMock);
        var procDetector = new RemoteControlProcessDetector(procMock);
        var evaluator = new SecurityPolicyEvaluator(sessionDetector, procDetector);

        var result = evaluator.EvaluateCompliance();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(PolicyEnforcementAction.BlockEntry, evaluator.DetermineAction(result, isExamActive: false));
        Assert.Equal(PolicyEnforcementAction.RestrictActiveExam, evaluator.DetermineAction(result, isExamActive: true));
    }

    [Fact]
    public void SecurityWatchdog_FiresRemoteAccessViolationDetected()
    {
        var sessionMock = new MockSessionQueryProvider
        {
            IsRemoteSession = true,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "RDP-Tcp#1", ClientName = "ATTACKER-PC", ProtocolType = 2 }
        };

        var procMock = new MockProcessProvider();
        var evaluator = new SecurityPolicyEvaluator(new RemoteSessionDetector(sessionMock), new RemoteControlProcessDetector(procMock));

        using var watchdog = new SecurityWatchdog(policyEvaluator: evaluator);
        var immediateResult = watchdog.CheckImmediateCompliance();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, immediateResult.Decision);
        Assert.Equal(DetectionCategory.WindowsRemoteDesktop, immediateResult.Category);
        Assert.Contains("ATTACKER-PC", immediateResult.Details);
    }

    [Fact]
    public void RemoteSessionDetector_WhenRemoteControlMetricSet_ReturnsConfirmedViolation()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            IsRemoteControl = true,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "Console", ProtocolType = 0 }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.WindowsRemoteDesktop, result.Category);
        Assert.Contains("Remote Desktop Protocol", result.Details);
    }

    [Fact]
    public void RemoteSessionDetector_WhenWtsProtocolIsIca_ReturnsConfirmedViolation()
    {
        var mock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            SessionInfo = new WtsSessionInfo
            {
                QuerySucceeded = true,
                WinStationName = "ICA-0",
                ClientName = "CITRIX-CLIENT",
                ProtocolType = 1 // Citrix ICA
            }
        };

        var detector = new RemoteSessionDetector(mock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.WindowsRemoteDesktop, result.Category);
        Assert.Contains("Citrix ICA", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenRustDeskRunning_ReturnsViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(1100, "rustdesk", "RustDesk Remote Access", SessionId: 1)
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.RemoteControlSoftware, result.Category);
        Assert.Contains("rustdesk", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenParsecRunning_ReturnsViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(1200, "parsecd", "Parsec Service", SessionId: 0),
                new(1201, "parsec", "Parsec Interactive Client", SessionId: 1)
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.RemoteControlSoftware, result.Category);
        Assert.Contains("parsec", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenMultipleProhibitedAppsRunning_AggregatesAll()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(2001, "anydesk", "AnyDesk"),
                new(2002, "teamviewer", "TeamViewer"),
                new(2003, "ultraviewer", "UltraViewer")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Contains("anydesk", result.Details);
        Assert.Contains("teamviewer", result.Details);
        Assert.Contains("ultraviewer", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenSession0DaemonRunningWithoutMetric_ReturnsReviewRequired()
    {
        var procMock = new MockProcessProvider
        {
            CurrentSessionId = 1,
            Processes = new List<RunningProcessInfo>
            {
                new(3001, "remoting_host", "Chrome Remote Desktop Host Service", SessionId: 0)
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate(hasActiveRemoteSessionMetric: false);

        Assert.Equal(SecurityPolicyDecision.ReviewRequired, result.Decision);
        Assert.Equal(EvidenceClassification.BackgroundService, result.Classification);
        Assert.Contains("remoting_host", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenSession0DaemonWithRemoteMetric_ReturnsConfirmedViolation()
    {
        var procMock = new MockProcessProvider
        {
            CurrentSessionId = 1,
            Processes = new List<RunningProcessInfo>
            {
                new(3001, "remoting_host", "Chrome Remote Desktop Host Service", SessionId: 0)
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate(hasActiveRemoteSessionMetric: true);

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(EvidenceClassification.CorroboratedViolation, result.Classification);
        Assert.Contains("remoting_host", result.Details);
    }

    [Fact]
    public void SecurityPolicyEvaluator_WhenStrictDaemonBlocking_EscalatesToConfirmedViolation()
    {
        var sessionMock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "Console" }
        };

        var procMock = new MockProcessProvider
        {
            CurrentSessionId = 1,
            Processes = new List<RunningProcessInfo>
            {
                new(4001, "remoting_host", "Chrome Remote Desktop Host Service", SessionId: 0)
            }
        };

        var sessionDetector = new RemoteSessionDetector(sessionMock);
        var procDetector = new RemoteControlProcessDetector(procMock);
        var evaluator = new SecurityPolicyEvaluator(sessionDetector, procDetector, strictDaemonBlocking: true);

        var result = evaluator.EvaluateCompliance();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(PolicyEnforcementAction.BlockEntry, evaluator.DetermineAction(result, isExamActive: false));
    }

    [Fact]
    public void SecurityPolicyEvaluator_WhenCleanEnvironment_ReturnsPermit()
    {
        var sessionMock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "Console", ProtocolType = 0 }
        };

        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(1, "explorer", "Windows Explorer")
            }
        };

        var evaluator = new SecurityPolicyEvaluator(new RemoteSessionDetector(sessionMock), new RemoteControlProcessDetector(procMock));
        var result = evaluator.EvaluateCompliance();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.Equal(PolicyEnforcementAction.Permit, evaluator.DetermineAction(result, isExamActive: false));
        Assert.Equal(PolicyEnforcementAction.Permit, evaluator.DetermineAction(result, isExamActive: true));
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenRemotingDesktopRunning_ReturnsConfirmedViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(901, "remoting_desktop", "Chrome Remote Desktop Host Agent")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.RemoteControlSoftware, result.Category);
        Assert.Contains("remoting_desktop", result.Details);
    }

    [Fact]
    public void RemoteControlProcessDetector_WhenRemoteAssistanceHostRunning_ReturnsConfirmedViolation()
    {
        var procMock = new MockProcessProvider
        {
            Processes = new List<RunningProcessInfo>
            {
                new(902, "remote_assistance_host", "Chrome Remote Desktop Assistance")
            }
        };

        var detector = new RemoteControlProcessDetector(procMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.RemoteControlSoftware, result.Category);
        Assert.Contains("remote_assistance_host", result.Details);
    }

    [Fact]
    public void WindowBannerDetector_WhenSharedDesktopBannerPresent_ReturnsConfirmedViolation()
    {
        var winMock = new MockWindowQueryProvider
        {
            Windows = new List<WindowInfo>
            {
                new(new IntPtr(1001), 18392, "remoting_desktop", "Your desktop is currently shared with pvenkatsai333@gmail.com", "Chrome_WidgetWin_1")
            }
        };

        var detector = new WindowBannerDetector(winMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.ScreenSharing, result.Category);
        Assert.Contains("Your desktop is currently shared with", result.Details);
    }

    [Fact]
    public void WindowBannerDetector_WhenNoBannerPresent_ReturnsClear()
    {
        var winMock = new MockWindowQueryProvider
        {
            Windows = new List<WindowInfo>
            {
                new(new IntPtr(1002), 5000, "Klyro.App", "Klyro Secure Assessment Browser", "WinUIDesktopWin32WindowClass"),
                new(new IntPtr(1003), 6000, "codechef", "CodeChef - Online Assessment", "Chrome_WidgetWin_1")
            }
        };

        var detector = new WindowBannerDetector(winMock);
        var result = detector.Evaluate();

        Assert.Equal(SecurityPolicyDecision.Clear, result.Decision);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public void SecurityPolicyEvaluator_SynthesizesWindowBannerViolation()
    {
        var sessionMock = new MockSessionQueryProvider
        {
            IsRemoteSession = false,
            SessionInfo = new WtsSessionInfo { QuerySucceeded = true, WinStationName = "Console", ProtocolType = 0 }
        };

        var procMock = new MockProcessProvider();
        var winMock = new MockWindowQueryProvider
        {
            Windows = new List<WindowInfo>
            {
                new(new IntPtr(2001), 18392, "remoting_desktop", "Your desktop is currently shared with test@example.com", "Chrome_WidgetWin_1")
            }
        };

        var evaluator = new SecurityPolicyEvaluator(
            new RemoteSessionDetector(sessionMock),
            new RemoteControlProcessDetector(procMock),
            new WindowBannerDetector(winMock));

        var result = evaluator.EvaluateCompliance();

        Assert.Equal(SecurityPolicyDecision.ConfirmedViolation, result.Decision);
        Assert.Equal(DetectionCategory.ScreenSharing, result.Category);
        Assert.Equal(PolicyEnforcementAction.RestrictActiveExam, evaluator.DetermineAction(result, isExamActive: true));
    }
}
