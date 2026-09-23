using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Network;
using Klyro.Core.Persistence;

namespace Klyro.Core.Tabs;

/// <summary>
/// Dedicated background worker thread for Tab 1 (Assessment Platform - CodeChef).
/// Handles asynchronous session snapshot auto-saves, assessment endpoint responsiveness checks,
/// and tab health reporting completely decoupled from the main UI thread.
/// </summary>
public sealed class AssessmentTabWorker : ITabWorker
{
    private readonly KlyroConfiguration _config;
    private readonly ISessionStore? _sessionStore;
    private readonly IAuditLogger? _auditLogger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wakeUpEvent = new(false);

    private Thread? _thread;
    private bool _isDisposed;
    private volatile bool _isRunning;
    private string _lastStatus = "Idle";
    private SessionRecord? _pendingSessionSnapshot;
    private readonly object _snapshotLock = new();

    public string TabId => "Tab_Assessment";
    public string Name => "CodeChef Assessment Tab";
    public bool IsRunning => _isRunning;
    public Thread? WorkerThread => _thread;
    public string LastStatus => _lastStatus;

    public event EventHandler<string>? StatusChanged;

    public AssessmentTabWorker(
        KlyroConfiguration config,
        ISessionStore? sessionStore = null,
        IAuditLogger? auditLogger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sessionStore = sessionStore;
        _auditLogger = auditLogger;
    }

    public void Start()
    {
        if (_thread != null) return;

        _isRunning = true;
        _thread = new Thread(RunAssessmentWorkerLoop)
        {
            Name = "Klyro_Tab_Assessment",
            IsBackground = true,
            Priority = ThreadPriority.Normal
        };
        _thread.Start();
        UpdateStatus("Started on dedicated thread");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts.Cancel();
        _wakeUpEvent.Set();
    }

    /// <summary>
    /// Thread-safe submission of session record for asynchronous disk persistence.
    /// Never blocks caller or UI thread.
    /// </summary>
    public void QueueSessionSnapshot(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_snapshotLock)
        {
            _pendingSessionSnapshot = record;
        }
        _wakeUpEvent.Set();
    }

    private void RunAssessmentWorkerLoop()
    {
        using var probe = new ConnectivityProbe();
        var assessmentUrl = _config.Exam.StartUrl;
        int checkCounter = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // 1. Process any pending session snapshot persistence off-thread
                SessionRecord? snapshotToSave = null;
                lock (_snapshotLock)
                {
                    if (_pendingSessionSnapshot != null)
                    {
                        snapshotToSave = _pendingSessionSnapshot;
                        _pendingSessionSnapshot = null;
                    }
                }

                if (snapshotToSave != null && _sessionStore != null)
                {
                    try
                    {
                        _sessionStore.SaveSession(snapshotToSave);
                        UpdateStatus($"Session auto-saved at {DateTime.UtcNow:HH:mm:ss}");
                    }
                    catch (Exception ex)
                    {
                        _auditLogger?.Log(EventType.SECURITY_VIOLATION, "AUTOSAVE", $"Failed to auto-save session: {ex.Message}");
                    }
                }

                // 2. Periodic Assessment Endpoint Health Watchdog (every 10 seconds)
                checkCounter++;
                if (checkCounter >= 10)
                {
                    checkCounter = 0;
                    try
                    {
                        var probeResult = probe.ExecuteAsync(ProbeType.AssessmentProvider, assessmentUrl, 3000, _cts.Token).GetAwaiter().GetResult();
                        if (probeResult.IsSuccess)
                        {
                            UpdateStatus($"Assessment active ({probeResult.LatencyMs}ms)");
                        }
                    }
                    catch { }
                }

                // Wait 1 second or until explicitly awakened by QueueSessionSnapshot
                _wakeUpEvent.Wait(1000, _cts.Token);
                _wakeUpEvent.Reset();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(EventType.EXAM_STARTED, "TAB_ASSESSMENT_ERROR", $"Assessment tab worker exception: {ex.Message}");
            }
        }

        UpdateStatus("Stopped");
    }

    private void UpdateStatus(string status)
    {
        _lastStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        if (_thread != null && _thread.IsAlive)
        {
            _thread.Join(500);
        }

        _wakeUpEvent.Dispose();
        _cts.Dispose();
    }
}
