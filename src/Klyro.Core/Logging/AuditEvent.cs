using System;
using System.Text.Json.Serialization;

namespace Klyro.Core.Logging;

/// <summary>
/// Immutable structured audit event. Zero password or credential fields.
/// </summary>
public sealed class AuditEvent
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("machineId")]
    public string MachineId { get; init; } = Environment.MachineName;

    [JsonPropertyName("examId")]
    public string? ExamId { get; init; }

    [JsonPropertyName("eventType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventType EventType { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "UNKNOWN";

    [JsonPropertyName("details")]
    public string Details { get; init; } = string.Empty;

    [JsonPropertyName("targetUrl")]
    public string? TargetUrl { get; init; }

    public override string ToString() =>
        $"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} Machine: {MachineId} Event: {EventType} State: {State} Details: {Details}";
}
