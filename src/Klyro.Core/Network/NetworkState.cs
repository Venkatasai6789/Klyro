namespace Klyro.Core.Network;

/// <summary>
/// Deterministic states of the Klyro network engine.
/// </summary>
public enum NetworkState
{
    STARTING,
    CHECKING,
    ONLINE,
    DEGRADED,
    PORTAL_REQUIRED,
    OFFLINE,
    ASSESSMENT_SERVICE_UNAVAILABLE,
    UNKNOWN
}
