using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Authentication;

namespace Klyro.Core.Preflight;

/// <summary>
/// Service contract for executing genuine, non-faked environment diagnostics.
/// </summary>
public interface IEnvironmentPreflightService
{
    /// <summary>
    /// Event fired whenever an individual check's state or diagnostic message changes.
    /// </summary>
    event EventHandler<PreflightItemProgress>? ItemProgressChanged;

    /// <summary>
    /// Current snapshot of all preflight checks.
    /// </summary>
    IReadOnlyList<PreflightItemProgress> CurrentItems { get; }

    /// <summary>
    /// Executes all configured environment checks in deterministic diagnostic sequence.
    /// </summary>
    Task<bool> RunAllChecksAsync(
        AuthenticatedUser? user,
        IProgress<PreflightProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reruns an individual check that previously failed or needs re-verification.
    /// </summary>
    Task<PreflightItemProgress> RerunCheckAsync(
        PreflightCheckId checkId,
        AuthenticatedUser? user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets all items to their initial waiting state.
    /// </summary>
    void Reset();
}
