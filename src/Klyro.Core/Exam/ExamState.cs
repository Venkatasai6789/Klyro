namespace Klyro.Core.Exam;

/// <summary>
/// Lifecycle states of an examination session.
/// </summary>
public enum ExamState
{
    INITIALIZING,
    PREFLIGHT,
    READY,
    ACTIVE,
    DEGRADED,
    NETWORK_RECOVERY,
    PAUSED,
    RESUMING,
    COMPLETED,
    FAILED,
    RECOVERY_REQUIRED
}
