using System;
using System.Collections.Generic;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Persistence;

namespace Klyro.Core.Tabs;

/// <summary>
/// Coordinates multi-threaded tab background workers across the application lifecycle.
/// </summary>
public sealed class TabWorkerManager : IDisposable
{
    private readonly List<ITabWorker> _workers = new();
    private bool _isDisposed;

    public AssessmentTabWorker AssessmentWorker { get; }
    public WifiPortalTabWorker WifiPortalWorker { get; }
    public IReadOnlyList<ITabWorker> Workers => _workers;

    public TabWorkerManager(
        KlyroConfiguration config,
        ISessionStore? sessionStore = null,
        IAuditLogger? auditLogger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        AssessmentWorker = new AssessmentTabWorker(config, sessionStore, auditLogger);
        WifiPortalWorker = new WifiPortalTabWorker(config, auditLogger);

        _workers.Add(AssessmentWorker);
        _workers.Add(WifiPortalWorker);
    }

    public void StartAll()
    {
        foreach (var worker in _workers)
        {
            worker.Start();
        }
    }

    public void StopAll()
    {
        foreach (var worker in _workers)
        {
            worker.Stop();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var worker in _workers)
        {
            worker.Dispose();
        }
    }
}
