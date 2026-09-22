using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Network;

namespace Klyro.Core.Recovery;

public delegate void RecoveryStateChangedEventHandler(RecoveryState previousState, RecoveryState newState);

/// <summary>
/// Orchestrates the captive portal recovery overlay lifecycle without interrupting the primary exam WebView.
/// </summary>
public sealed class RecoveryController : IDisposable
{
    private readonly object _lock = new();
    private readonly INetworkMonitor _networkMonitor;
    private readonly PortalAuthenticationMonitor _authMonitor;
    private readonly IAuditLogger? _auditLogger;
    private CancellationTokenSource? _activeRecoveryCts;

    public RecoveryState CurrentState { get; private set; } = RecoveryState.Hidden;

    public event RecoveryStateChangedEventHandler? StateChanged;
    public event EventHandler? ExamResumeRequested;

    public RecoveryController(
        INetworkMonitor networkMonitor,
        NetworkOptions networkOptions,
        IAuditLogger? auditLogger = null)
    {
        _networkMonitor = networkMonitor ?? throw new ArgumentNullException(nameof(networkMonitor));
        _auditLogger = auditLogger;
        _authMonitor = new PortalAuthenticationMonitor(_networkMonitor, networkOptions, _auditLogger);

        _networkMonitor.StateChanged += OnNetworkStateChanged;
    }

    private void OnNetworkStateChanged(NetworkState previousState, NetworkState newState, ProbeMatrix matrix)
    {
        lock (_lock)
        {
            if (newState == NetworkState.PORTAL_REQUIRED && (CurrentState == RecoveryState.Hidden || CurrentState == RecoveryState.ActiveInBackground))
            {
                TransitionTo(RecoveryState.LoginRequired);
                _auditLogger?.Log(EventType.RECOVERY_UI_SHOWN, CurrentState.ToString(), "Captive portal recovery modal displayed.");
            }
            else if (newState == NetworkState.ONLINE && CurrentState != RecoveryState.Hidden && CurrentState != RecoveryState.ActiveInBackground)
            {
                TransitionTo(RecoveryState.ConnectionRestored);
                _auditLogger?.Log(EventType.RECOVERY_UI_DISMISSED, CurrentState.ToString(), "Network online confirmed; keeping portal session active in background.");
                TransitionTo(RecoveryState.ActiveInBackground);
                ExamResumeRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Invoked when the user submits authentication credentials inside the recovery WebView.
    /// Initiates independent background verification.
    /// </summary>
    public async Task<bool> HandleCredentialsSubmittedAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            TransitionTo(RecoveryState.Authenticating);
            _activeRecoveryCts?.Cancel();
            _activeRecoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        bool success = await _authMonitor.VerifyConnectionRestoredAsync(_activeRecoveryCts.Token);

        lock (_lock)
        {
            if (success)
            {
                TransitionTo(RecoveryState.ConnectionRestored);
                TransitionTo(RecoveryState.ActiveInBackground);
                ExamResumeRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }
            else
            {
                TransitionTo(RecoveryState.RecoveryFailed);
                return false;
            }
        }
    }

    /// <summary>
    /// Explicitly transitions the recovery UI to active background state,
    /// keeping the captive portal session alive without closing its process.
    /// </summary>
    public void KeepActiveInBackground()
    {
        lock (_lock)
        {
            if (CurrentState != RecoveryState.ActiveInBackground)
            {
                TransitionTo(RecoveryState.ActiveInBackground);
                ExamResumeRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Invoked by the student when clicking "Retry" on a failed recovery screen.
    /// </summary>
    public void Retry()
    {
        lock (_lock)
        {
            TransitionTo(RecoveryState.LoginRequired);
        }
    }

    private void TransitionTo(RecoveryState newState)
    {
        if (CurrentState == newState) return;

        var oldState = CurrentState;
        CurrentState = newState;
        StateChanged?.Invoke(oldState, newState);
    }

    public void Dispose()
    {
        _networkMonitor.StateChanged -= OnNetworkStateChanged;
        _activeRecoveryCts?.Cancel();
        _activeRecoveryCts?.Dispose();
    }
}
