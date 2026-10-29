using System;
using System.Text.Json.Serialization;
using Klyro.Core.Security.RemoteAccess;

namespace Klyro.Core.Incidents;

public sealed class SecurityIncident
{
    [JsonPropertyName("incidentId")]
    public string IncidentId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("examId")]
    public string? ExamId { get; init; }

    [JsonPropertyName("candidateEmail")]
    public string? CandidateEmail { get; init; }

    [JsonPropertyName("machineId")]
    public string MachineId { get; init; } = Environment.MachineName;

    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DetectionCategory Category { get; init; }

    [JsonPropertyName("classification")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EvidenceClassification Classification { get; init; }

    [JsonPropertyName("details")]
    public string Details { get; init; } = string.Empty;

    [JsonPropertyName("decision")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SecurityPolicyDecision Decision { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("restrictionState")]
    public string RestrictionState { get; set; } = "RESTRICTED";

    [JsonPropertyName("reviewStatus")]
    public string ReviewStatus { get; set; } = "PENDING_REVIEW"; // PENDING_REVIEW, APPROVED_RECHECK, REJECTED, RESOLVED

    [JsonPropertyName("reviewNotes")]
    public string? ReviewNotes { get; set; }

    [JsonPropertyName("reviewedBy")]
    public string? ReviewedBy { get; set; }

    [JsonPropertyName("reviewedAtUtc")]
    public DateTimeOffset? ReviewedAtUtc { get; set; }

    [JsonPropertyName("isResolved")]
    public bool IsResolved { get; set; }
}
