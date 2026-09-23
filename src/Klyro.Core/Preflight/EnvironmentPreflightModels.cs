using System;

namespace Klyro.Core.Preflight;

public enum PreflightCheckId
{
    Account,
    Network,
    Internet,
    AssessmentService,
    SecureEnvironment
}

public enum PreflightCheckState
{
    Waiting,
    Checking,
    Passed,
    Warning,
    Failed
}

/// <summary>
/// State and diagnostic details for an individual environment preflight item.
/// </summary>
public sealed class PreflightItemProgress
{
    public PreflightCheckId Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public PreflightCheckState State { get; set; } = PreflightCheckState.Waiting;
    public long LatencyMs { get; set; }
    public string? DiagnosticDetails { get; set; }
    public bool IsCritical { get; init; } = true;

    public PreflightItemProgress Clone() => new()
    {
        Id = Id,
        Title = Title,
        Subtitle = Subtitle,
        State = State,
        LatencyMs = LatencyMs,
        DiagnosticDetails = DiagnosticDetails,
        IsCritical = IsCritical
    };
}

/// <summary>
/// Progress snapshot across the overall preflight sequence.
/// </summary>
public sealed class PreflightProgressReport
{
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    public bool IsAllFinished { get; init; }
    public bool AllPassed { get; init; }
    public PreflightItemProgress? LatestUpdate { get; init; }
}
