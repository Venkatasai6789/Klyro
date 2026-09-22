namespace Klyro.Core.Recovery;

/// <summary>
/// Discrete presentation states of the captive portal recovery UI.
/// </summary>
public enum RecoveryState
{
    Hidden,
    CheckingConnection,
    LoginRequired,
    Authenticating,
    ConnectionRestored,
    RecoveryFailed,
    ActiveInBackground
}
