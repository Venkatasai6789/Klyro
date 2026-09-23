using System;
using System.Threading;

namespace Klyro.Core.Tabs;

/// <summary>
/// Defines the lifecycle and interface of a tab running background tasks on a dedicated OS thread.
/// </summary>
public interface ITabWorker : IDisposable
{
    string TabId { get; }
    string Name { get; }
    bool IsRunning { get; }
    Thread? WorkerThread { get; }

    event EventHandler<string>? StatusChanged;

    void Start();
    void Stop();
}
