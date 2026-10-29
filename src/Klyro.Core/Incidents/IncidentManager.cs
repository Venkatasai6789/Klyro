using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Klyro.Core.Logging;
using Klyro.Core.Security.RemoteAccess;

namespace Klyro.Core.Incidents;

public sealed class IncidentManager
{
    private static readonly Lazy<IncidentManager> LazyInstance = new(() => new IncidentManager());
    public static IncidentManager Instance => LazyInstance.Value;

    private readonly ConcurrentDictionary<string, SecurityIncident> _incidents = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentDedupeKeys = new();
    private readonly IAuditLogger? _auditLogger;
    private readonly string? _storageDirectory;

    public IncidentManager(IAuditLogger? auditLogger = null, string? customDirectory = null)
    {
        _auditLogger = auditLogger;
        _storageDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klyro",
            "Incidents");

        try
        {
            Directory.CreateDirectory(_storageDirectory);
            LoadPersistedIncidents();
        }
        catch { }
    }

    public SecurityIncident RecordIncident(
        RemoteAccessDetectionResult detectionResult,
        string sessionId,
        string? examId = null,
        string? candidateEmail = null)
    {
        string dedupeKey = $"{sessionId}:{detectionResult.Category}:{detectionResult.Details}";

        // Deduplicate events within 10 seconds to avoid flooding
        if (_recentDedupeKeys.TryGetValue(dedupeKey, out var lastTime))
        {
            if (DateTimeOffset.UtcNow - lastTime < TimeSpan.FromSeconds(10))
            {
                var existing = _incidents.Values
                    .Where(i => i.SessionId == sessionId && i.Category == detectionResult.Category)
                    .OrderByDescending(i => i.CreatedAtUtc)
                    .FirstOrDefault();

                if (existing != null)
                {
                    return existing;
                }
            }
        }

        _recentDedupeKeys[dedupeKey] = DateTimeOffset.UtcNow;

        var incident = new SecurityIncident
        {
            SessionId = sessionId,
            ExamId = examId,
            CandidateEmail = candidateEmail,
            Category = detectionResult.Category,
            Classification = detectionResult.Classification,
            Details = detectionResult.Details,
            Decision = detectionResult.Decision,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RestrictionState = "RESTRICTED",
            ReviewStatus = "PENDING_REVIEW"
        };

        _incidents[incident.IncidentId] = incident;

        _auditLogger?.Log(
            EventType.SECURITY_RESTRICTED,
            "INCIDENT_RECORDED",
            $"Incident {incident.IncidentId} recorded: [Category={incident.Category}] {incident.Details}",
            examId: examId);

        PersistIncident(incident);
        return incident;
    }

    public IReadOnlyList<SecurityIncident> GetAllIncidents()
    {
        return _incidents.Values.OrderByDescending(i => i.CreatedAtUtc).ToList();
    }

    public SecurityIncident? GetIncident(string incidentId)
    {
        _incidents.TryGetValue(incidentId, out var incident);
        return incident;
    }

    public bool SubmitReviewRequest(string incidentId, string explanation)
    {
        if (_incidents.TryGetValue(incidentId, out var incident))
        {
            incident.ReviewNotes = explanation;
            incident.ReviewStatus = "PENDING_REVIEW";

            _auditLogger?.Log(
                EventType.REVIEW_REQUESTED,
                "REVIEW_REQUESTED",
                $"Candidate requested review for incident {incidentId}: {explanation}",
                examId: incident.ExamId);

            PersistIncident(incident);
            return true;
        }
        return false;
    }

    public bool AuthorizeRecovery(string incidentId, string authorizedBy, string? decisionNotes = null)
    {
        if (_incidents.TryGetValue(incidentId, out var incident))
        {
            incident.ReviewStatus = "APPROVED_RECHECK";
            incident.ReviewedBy = authorizedBy;
            incident.ReviewNotes = decisionNotes ?? "Administrator authorized fresh environment verification.";
            incident.ReviewedAtUtc = DateTimeOffset.UtcNow;

            _auditLogger?.Log(
                EventType.RECOVERY_AUTHORIZED,
                "RECHECK_AUTHORIZED",
                $"Recheck authorized for incident {incidentId} by {authorizedBy}",
                examId: incident.ExamId);

            PersistIncident(incident);
            return true;
        }
        return false;
    }

    public bool ResolveIncident(string incidentId, string authorizedBy)
    {
        if (_incidents.TryGetValue(incidentId, out var incident))
        {
            incident.IsResolved = true;
            incident.ReviewStatus = "RESOLVED";
            incident.RestrictionState = "NORMAL";
            incident.ReviewedBy = authorizedBy;
            incident.ReviewedAtUtc = DateTimeOffset.UtcNow;

            PersistIncident(incident);
            return true;
        }
        return false;
    }

    private void PersistIncident(SecurityIncident incident)
    {
        if (string.IsNullOrWhiteSpace(_storageDirectory)) return;
        try
        {
            string filePath = Path.Combine(_storageDirectory, $"{incident.IncidentId}.json");
            string json = JsonSerializer.Serialize(incident, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch { }
    }

    private void LoadPersistedIncidents()
    {
        if (string.IsNullOrWhiteSpace(_storageDirectory) || !Directory.Exists(_storageDirectory)) return;
        try
        {
            var files = Directory.GetFiles(_storageDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var incident = JsonSerializer.Deserialize<SecurityIncident>(json);
                    if (incident != null)
                    {
                        _incidents[incident.IncidentId] = incident;
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
