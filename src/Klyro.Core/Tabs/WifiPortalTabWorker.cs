using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Network;

namespace Klyro.Core.Tabs;

/// <summary>
/// Dedicated background worker thread for Tab 2 (Campus Wi-Fi Portal & Recovery).
/// Offloads CPU/Process-intensive Wi-Fi signal scanning (netsh.exe), portal session keep-alive heartbeats,
/// and captive portal connectivity checks from the UI thread to a dedicated background OS thread.
/// </summary>
public sealed class WifiPortalTabWorker : ITabWorker
{
    private readonly KlyroConfiguration _config;
    private readonly IAuditLogger? _auditLogger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wakeUpEvent = new(false);

    private Thread? _thread;
    private bool _isDisposed;
    private volatile bool _isRunning;
    private volatile bool _isRecoveryActive;
    private string _lastStatus = "Idle";
    private (int signalPercentage, string ssid) _latestWifiInfo = (92, "KLU_WIFI");

    public string TabId => "Tab_WifiPortal";
    public string Name => "Campus Wi-Fi Portal Tab";
    public bool IsRunning => _isRunning;
    public Thread? WorkerThread => _thread;
    public string LastStatus => _lastStatus;
    public (int signalPercentage, string ssid) LatestWifiInfo => _latestWifiInfo;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(int signalPercentage, string ssid)>? WifiSignalUpdated;
    public event EventHandler? InternetRestored;

    public WifiPortalTabWorker(
        KlyroConfiguration config,
        IAuditLogger? auditLogger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _auditLogger = auditLogger;
    }

    public void Start()
    {
        if (_thread != null) return;

        _isRunning = true;
        _thread = new Thread(RunWifiPortalWorkerLoop)
        {
            Name = "Klyro_Tab_WifiPortal",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
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

    public void SetRecoveryMode(bool isActive)
    {
        _isRecoveryActive = isActive;
        _wakeUpEvent.Set();
    }

    private void RunWifiPortalWorkerLoop()
    {
        using var probe = new ConnectivityProbe();
        var portalUrl = _config.Network.PortalUrl;
        int loopCount = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                loopCount++;

                // 1. Offload Wi-Fi Signal Query from UI thread (every 2-3 seconds)
                if (loopCount % 2 == 0)
                {
                    var wifiInfo = WifiSignalService.GetCurrentWifiSignal();
                    _latestWifiInfo = wifiInfo;
                    WifiSignalUpdated?.Invoke(this, wifiInfo);
                }

                // 2. If Recovery Mode is active, poll Internet connectivity fast (every 1 second)
                if (_isRecoveryActive)
                {
                    try
                    {
                        var probeResult = probe.ExecuteAsync(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 1200, _cts.Token).GetAwaiter().GetResult();
                        if (probeResult.IsSuccess)
                        {
                            UpdateStatus("Internet connection restored via portal");
                            InternetRestored?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch { }
                }
                else
                {
                    // 3. Normal Mode: Background Portal Keep-Alive (every 30 seconds)
                    if (loopCount % 30 == 0 && !string.IsNullOrWhiteSpace(portalUrl))
                    {
                        try
                        {
                            var portalCheck = probe.ExecuteAsync(ProbeType.CaptivePortal, portalUrl, 3000, _cts.Token).GetAwaiter().GetResult();
                            UpdateStatus(portalCheck.IsSuccess ? "Portal session active in background" : "Portal standby");
                        }
                        catch { }
                    }
                }

                // Wait 1 second (or wake up immediately on mode switch)
                _wakeUpEvent.Wait(1000, _cts.Token);
                _wakeUpEvent.Reset();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(EventType.EXAM_STARTED, "TAB_WIFI_ERROR", $"Wi-Fi portal tab worker exception: {ex.Message}");
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
