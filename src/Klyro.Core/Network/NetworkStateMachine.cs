using System;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;

namespace Klyro.Core.Network;

public delegate void NetworkStateChangedEventHandler(NetworkState previousState, NetworkState newState, ProbeMatrix matrix);

/// <summary>
/// Thread-safe deterministic state machine managing Klyro network transitions.
/// </summary>
public sealed class NetworkStateMachine
{
    private readonly object _lock = new();
    private readonly CaptivePortalDetector _detector;
    private readonly IAuditLogger? _auditLogger;

    public NetworkState CurrentState { get; private set; } = NetworkState.STARTING;
    public NetworkState PreviousState { get; private set; } = NetworkState.STARTING;
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset LastStateChangeTime { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastOnlineTime { get; private set; }

    public event NetworkStateChangedEventHandler? StateChanged;

    public NetworkStateMachine(NetworkOptions networkOptions, IAuditLogger? auditLogger = null)
    {
        ArgumentNullException.ThrowIfNull(networkOptions);
        _detector = new CaptivePortalDetector(networkOptions);
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Processes a new probe matrix snapshot and updates state accordingly.
    /// </summary>
    public NetworkState ProcessProbeMatrix(ProbeMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        NetworkState oldState;
        NetworkState newState;
        bool stateChanged = false;

        lock (_lock)
        {
            oldState = CurrentState;

            // Track consecutive failures
            if (!matrix.Internet.IsSuccess)
            {
                ConsecutiveFailures++;
            }
            else
            {
                ConsecutiveFailures = 0;
            }

            newState = _detector.Evaluate(matrix, ConsecutiveFailures);

            if (newState != oldState)
            {
                PreviousState = oldState;
                CurrentState = newState;
                LastStateChangeTime = DateTimeOffset.UtcNow;
                stateChanged = true;

                if (newState == NetworkState.ONLINE)
                {
                    LastOnlineTime = DateTimeOffset.UtcNow;
                }
            }
        }

        if (stateChanged)
        {
            LogTransition(oldState, newState);
            StateChanged?.Invoke(oldState, newState, matrix);
        }

        return newState;
    }

    private void LogTransition(NetworkState oldState, NetworkState newState)
    {
        var eventType = newState switch
        {
            NetworkState.ONLINE => EventType.NETWORK_RESTORED,
            NetworkState.DEGRADED => EventType.NETWORK_DEGRADED,
            NetworkState.PORTAL_REQUIRED => EventType.CAPTIVE_PORTAL_DETECTED,
            NetworkState.OFFLINE => EventType.NETWORK_LOST,
            NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE => EventType.CODECHEF_UNAVAILABLE,
            _ => EventType.NETWORK_DEGRADED
        };

        _auditLogger?.Log(eventType, newState.ToString(), $"Transitioned from {oldState} to {newState}");
    }
}
