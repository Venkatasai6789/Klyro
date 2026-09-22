using System;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Network;

/// <summary>
/// Service interface for background continuous network monitoring.
/// </summary>
public interface INetworkMonitor : IDisposable
{
    NetworkState CurrentState { get; }
    NetworkStateMachine StateMachine { get; }
    INetworkSimulator Simulator { get; }

    event NetworkStateChangedEventHandler? StateChanged;

    void Start();
    void Stop();
    Task<NetworkState> TriggerImmediateProbeAsync(CancellationToken cancellationToken = default);
}
