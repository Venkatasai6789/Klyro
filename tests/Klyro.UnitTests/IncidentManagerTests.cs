using System;
using System.IO;
using System.Linq;
using Klyro.Core.Incidents;
using Klyro.Core.Security.RemoteAccess;
using Xunit;

namespace Klyro.UnitTests;

public class IncidentManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly IncidentManager _manager;

    public IncidentManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Klyro_Incident_Tests_" + Guid.NewGuid().ToString("N"));
        _manager = new IncidentManager(customDirectory: _testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void RecordIncident_CreatesValidIncidentRecord()
    {
        var detection = new RemoteAccessDetectionResult
        {
            Decision = SecurityPolicyDecision.ConfirmedViolation,
            Category = DetectionCategory.WindowsRemoteDesktop,
            Classification = EvidenceClassification.ActiveRemoteSession,
            Details = "Active RDP session connected from 192.168.1.50"
        };

        var incident = _manager.RecordIncident(
            detection,
            sessionId: "test-session-123",
            examId: "contest-456",
            candidateEmail: "candidate@university.edu");

        Assert.NotNull(incident);
        Assert.Equal("test-session-123", incident.SessionId);
        Assert.Equal("contest-456", incident.ExamId);
        Assert.Equal("candidate@university.edu", incident.CandidateEmail);
        Assert.Equal(DetectionCategory.WindowsRemoteDesktop, incident.Category);
        Assert.Equal("RESTRICTED", incident.RestrictionState);
        Assert.Equal("PENDING_REVIEW", incident.ReviewStatus);
        Assert.False(incident.IsResolved);
    }

    [Fact]
    public void RecordIncident_DeduplicatesRapidRepeats()
    {
        var detection = new RemoteAccessDetectionResult
        {
            Decision = SecurityPolicyDecision.ConfirmedViolation,
            Category = DetectionCategory.RemoteControlSoftware,
            Details = "AnyDesk active"
        };

        var first = _manager.RecordIncident(detection, "sess-1");
        var second = _manager.RecordIncident(detection, "sess-1");

        Assert.Equal(first.IncidentId, second.IncidentId);
    }

    [Fact]
    public void SubmitReviewRequest_UpdatesStatusAndNotes()
    {
        var detection = new RemoteAccessDetectionResult
        {
            Decision = SecurityPolicyDecision.ConfirmedViolation,
            Category = DetectionCategory.RemoteControlSoftware,
            Details = "AnyDesk process detected"
        };

        var incident = _manager.RecordIncident(detection, "sess-2");
        bool updated = _manager.SubmitReviewRequest(incident.IncidentId, "I closed AnyDesk from taskbar.");

        Assert.True(updated);
        var retrieved = _manager.GetIncident(incident.IncidentId);
        Assert.NotNull(retrieved);
        Assert.Equal("I closed AnyDesk from taskbar.", retrieved.ReviewNotes);
    }

    [Fact]
    public void AuthorizeRecovery_MarksApprovedForRecheck()
    {
        var detection = new RemoteAccessDetectionResult
        {
            Decision = SecurityPolicyDecision.ConfirmedViolation,
            Category = DetectionCategory.WindowsRemoteDesktop,
            Details = "RDP violation"
        };

        var incident = _manager.RecordIncident(detection, "sess-3");
        bool authorized = _manager.AuthorizeRecovery(incident.IncidentId, "AdminUser", "Approved to recheck");

        Assert.True(authorized);
        var retrieved = _manager.GetIncident(incident.IncidentId);
        Assert.NotNull(retrieved);
        Assert.Equal("APPROVED_RECHECK", retrieved.ReviewStatus);
        Assert.Equal("AdminUser", retrieved.ReviewedBy);
        Assert.NotNull(retrieved.ReviewedAtUtc);
    }

    [Fact]
    public void ResolveIncident_MarksResolved()
    {
        var detection = new RemoteAccessDetectionResult
        {
            Decision = SecurityPolicyDecision.ConfirmedViolation,
            Category = DetectionCategory.RemoteControlSoftware,
            Details = "TeamViewer"
        };

        var incident = _manager.RecordIncident(detection, "sess-4");
        bool resolved = _manager.ResolveIncident(incident.IncidentId, "AdminUser");

        Assert.True(resolved);
        var retrieved = _manager.GetIncident(incident.IncidentId);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsResolved);
        Assert.Equal("RESOLVED", retrieved.ReviewStatus);
        Assert.Equal("NORMAL", retrieved.RestrictionState);
    }
}
