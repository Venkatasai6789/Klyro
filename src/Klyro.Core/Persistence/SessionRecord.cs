using System;
using System.Text.Json.Serialization;
using Klyro.Core.Exam;

namespace Klyro.Core.Persistence;

/// <summary>
/// Minimal persisted examination session record for crash and reboot recovery.
/// Strictly excludes credentials, tokens, keystrokes, clipboard buffers, and full history.
/// </summary>
public sealed class SessionRecord
{
    [JsonPropertyName("examId")]
    public string ExamId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = Environment.MachineName;

    [JsonPropertyName("startTimestampUtc")]
    public DateTimeOffset StartTimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("currentExamState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExamState CurrentExamState { get; set; } = ExamState.INITIALIZING;

    [JsonPropertyName("lastSuccessfulNetworkTimestampUtc")]
    public DateTimeOffset? LastSuccessfulNetworkTimestampUtc { get; set; }

    [JsonPropertyName("lastRecoveryEvent")]
    public string? LastRecoveryEvent { get; set; }

    [JsonPropertyName("lastKnownAssessmentUrl")]
    public string LastKnownAssessmentUrl { get; set; } = string.Empty;

    [JsonPropertyName("remainingSeconds")]
    public double RemainingSeconds { get; set; }

    [JsonPropertyName("isCompleted")]
    public bool IsCompleted { get; set; }
}
