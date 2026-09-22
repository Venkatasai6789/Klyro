namespace Klyro.Core.Network;

/// <summary>
/// Controllable simulation conditions for automated tests and development runs.
/// </summary>
public enum SimulatedNetworkCondition
{
    Normal,
    PortalRequired,
    Offline,
    Degraded,
    CodeChefUnavailable,
    PortalUnavailable,
    Flapping
}

/// <summary>
/// Interface for injecting simulated network conditions without hardware manipulation.
/// </summary>
public interface INetworkSimulator
{
    bool IsSimulationActive { get; set; }
    SimulatedNetworkCondition CurrentCondition { get; set; }
    ProbeMatrix GenerateSimulatedMatrix(string gatewayEndpoint, string portalEndpoint, string internetEndpoint, string assessmentEndpoint);
}
