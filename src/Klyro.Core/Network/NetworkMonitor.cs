using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;

namespace Klyro.Core.Network;

/// <summary>
/// Autonomous multi-threaded network engine that executes on a dedicated, prioritized OS thread.
/// Runs a high-precision 1ms monitoring loop to achieve sub-millisecond reaction speeds to link drops,
/// simulator modifications, and background probe results with lock-free atomic query latency.
/// </summary>
public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly KlyroConfiguration _config;
    private readonly IConnectivityProbe _probe;
    private readonly IAuditLogger? _auditLogger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wakeUpEvent = new(false);

    private Thread? _monitorThread;
    private bool _isDisposed;
    private long _totalTicks;
    private long _lastLatencyMs;
    private string _connectionSpeedTier = "Unknown";
    private volatile ProbeMatrix _latestMatrix = new();
    private Task<ProbeMatrix>? _inFlightProbeTask;
    private readonly object _probeLock = new();

    public NetworkStateMachine StateMachine { get; }
    public INetworkSimulator Simulator { get; } = new NetworkSimulator();

    public NetworkState CurrentState => StateMachine.CurrentState;
    public long LastLatencyMs => Interlocked.Read(ref _lastLatencyMs);
    public string ConnectionSpeedTier => Volatile.Read(ref _connectionSpeedTier);
    public ProbeMatrix LatestMatrix => _latestMatrix;
    public Thread? WorkerThread => _monitorThread;
    public long TotalTickCount => Interlocked.Read(ref _totalTicks);

    public event NetworkStateChangedEventHandler? StateChanged
    {
        add => StateMachine.StateChanged += value;
        remove => StateMachine.StateChanged -= value;
    }

    public event EventHandler<ProbeMatrix>? ProbeCompleted;

    public NetworkMonitor(
        KlyroConfiguration config,
        IConnectivityProbe? probe = null,
        IAuditLogger? auditLogger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _probe = probe ?? new ConnectivityProbe();
        _auditLogger = auditLogger;

        StateMachine = new NetworkStateMachine(_config.Network, _auditLogger);

        if (_config.Network.EnableNetworkSimulator)
        {
            Simulator.IsSimulationActive = true;
        }
    }

    public void Start()
    {
        if (_monitorThread != null) return;

        _monitorThread = new Thread(DedicatedMonitorLoop)
        {
            Name = "Klyro_NetworkMonitor_Engine",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _monitorThread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _wakeUpEvent.Set();
    }

    public async Task<NetworkState> TriggerImmediateProbeAsync(CancellationToken cancellationToken = default)
    {
        _wakeUpEvent.Set();
        var matrix = await ExecuteAllProbesAsync(cancellationToken);
        return ApplyProbeMatrix(matrix);
    }

    /// <summary>
    /// Dedicated 1ms high-frequency OS thread loop.
    /// Uses HighResolutionTimer (winmm.dll) to ensure 1ms precision sleep without burning 100% CPU.
    /// </summary>
    private void DedicatedMonitorLoop()
    {
        using var hiResTimer = new HighResolutionTimer(1);
        var stopwatch = Stopwatch.StartNew();
        long lastProbeTimeMs = 0;
        bool lastKnownPhysicalLink = true;

        while (!_cts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _totalTicks);
            long nowMs = stopwatch.ElapsedMilliseconds;

            try
            {
                // 1. Instant Hardware Link Check (Sub-millisecond link-drop reaction)
                bool isNetworkPhysicallyUp = NetworkInterface.GetIsNetworkAvailable();
                if (!isNetworkPhysicallyUp && lastKnownPhysicalLink)
                {
                    lastKnownPhysicalLink = false;
                    var offlineMatrix = new ProbeMatrix
                    {
                        Gateway = ProbeResult.Failure(ProbeType.Gateway, "0.0.0.0", "Physical network adapter disconnected"),
                        Internet = ProbeResult.Failure(ProbeType.InternetConnectivity, "none", "Network interface unavailable"),
                        CaptivePortal = ProbeResult.Failure(ProbeType.CaptivePortal, "none", "Network interface unavailable"),
                        AssessmentProvider = ProbeResult.Failure(ProbeType.AssessmentProvider, "none", "Network interface unavailable")
                    };
                    ApplyProbeMatrix(offlineMatrix);
                }
                else if (isNetworkPhysicallyUp)
                {
                    lastKnownPhysicalLink = true;
                }

                // 2. Process in-flight asynchronous probe if completed
                lock (_probeLock)
                {
                    if (_inFlightProbeTask != null && _inFlightProbeTask.IsCompleted)
                    {
                        try
                        {
                            if (_inFlightProbeTask.IsCompletedSuccessfully)
                            {
                                ApplyProbeMatrix(_inFlightProbeTask.Result);
                            }
                        }
                        catch { }
                        finally
                        {
                            _inFlightProbeTask = null;
                            lastProbeTimeMs = nowMs;
                        }
                    }
                }

                // 3. Check if time to launch a new probe
                int intervalMs = Math.Max(100, _config.Network.ProbeIntervalMs);
                bool shouldProbe = (nowMs - lastProbeTimeMs >= intervalMs) || _wakeUpEvent.IsSet;

                if (shouldProbe)
                {
                    if (_wakeUpEvent.IsSet)
                    {
                        _wakeUpEvent.Reset();
                    }

                    lock (_probeLock)
                    {
                        if (_inFlightProbeTask == null)
                        {
                            _inFlightProbeTask = Task.Run(() => ExecuteAllProbesAsync(_cts.Token));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(
                    EventType.NETWORK_DEGRADED,
                    StateMachine.CurrentState.ToString(),
                    $"Network monitor loop exception: {ex.Message}");
            }

            // 4. High-Precision 1ms Wait (Thread yield with 1ms resolution)
            try
            {
                _wakeUpEvent.Wait(1, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private NetworkState ApplyProbeMatrix(ProbeMatrix matrix)
    {
        _latestMatrix = matrix;

        if (matrix.Internet.IsSuccess)
        {
            Interlocked.Exchange(ref _lastLatencyMs, matrix.Internet.LatencyMs);
            string tier = matrix.Internet.LatencyMs switch
            {
                < 80 => "Fast",
                < 250 => "Good",
                _ => "Slow"
            };
            Volatile.Write(ref _connectionSpeedTier, tier);
        }
        else
        {
            Volatile.Write(ref _connectionSpeedTier, "Offline");
        }

        var state = StateMachine.ProcessProbeMatrix(matrix);
        ProbeCompleted?.Invoke(this, matrix);
        return state;
    }

    private async Task<ProbeMatrix> ExecuteAllProbesAsync(CancellationToken cancellationToken)
    {
        var gatewayEndpoint = ResolveDefaultGateway();
        var portalEndpoint = _config.Network.PortalUrl;
        var internetEndpoint = _config.Network.ConnectivityEndpoints.FirstOrDefault() ?? "http://clients3.google.com/generate_204";
        var assessmentEndpoint = _config.Exam.StartUrl;

        // If simulator is active, return simulated matrix
        if (Simulator.IsSimulationActive)
        {
            return Simulator.GenerateSimulatedMatrix(gatewayEndpoint, portalEndpoint, internetEndpoint, assessmentEndpoint);
        }

        var timeout = _config.Network.ProbeTimeoutMs;

        // Execute all 4 probes concurrently
        var gatewayTask = _probe is not ConnectivityProbe
            ? _probe.ExecuteAsync(ProbeType.Gateway, gatewayEndpoint, timeout, cancellationToken)
            : ProbeGatewayAsync(gatewayEndpoint, timeout, cancellationToken);
        var portalTask = _probe.ExecuteAsync(ProbeType.CaptivePortal, portalEndpoint, timeout, cancellationToken);
        var internetTask = _probe.ExecuteAsync(ProbeType.InternetConnectivity, internetEndpoint, timeout, cancellationToken);
        var assessmentTask = _probe.ExecuteAsync(ProbeType.AssessmentProvider, assessmentEndpoint, timeout, cancellationToken);

        await Task.WhenAll(gatewayTask, portalTask, internetTask, assessmentTask);

        return new ProbeMatrix
        {
            Gateway = await gatewayTask,
            CaptivePortal = await portalTask,
            Internet = await internetTask,
            AssessmentProvider = await assessmentTask
        };
    }

    private async Task<ProbeResult> ProbeGatewayAsync(string gatewayIp, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.Equals(gatewayIp, "0.0.0.0") || string.IsNullOrWhiteSpace(gatewayIp))
        {
            return ProbeResult.Failure(ProbeType.Gateway, gatewayIp, "No active network gateway found");
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(gatewayIp, timeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                return ProbeResult.Success(ProbeType.Gateway, gatewayIp, 200, reply.RoundtripTime);
            }
            return ProbeResult.Failure(ProbeType.Gateway, gatewayIp, $"Ping status: {reply.Status}", 0, reply.RoundtripTime);
        }
        catch (Exception ex)
        {
            return ProbeResult.Failure(ProbeType.Gateway, gatewayIp, ex.Message);
        }
    }

    private static string ResolveDefaultGateway()
    {
        try
        {
            var gateway = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties()?.GatewayAddresses ?? Enumerable.Empty<GatewayIPAddressInformation>())
                .Select(g => g?.Address?.ToString())
                .FirstOrDefault(g => !string.IsNullOrEmpty(g) && g != "0.0.0.0");

            return gateway ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _wakeUpEvent.Set();

        if (_monitorThread != null && _monitorThread.IsAlive)
        {
            _monitorThread.Join(500);
        }

        _wakeUpEvent.Dispose();
        _cts.Dispose();

        if (_probe is IDisposable disposableProbe)
        {
            disposableProbe.Dispose();
        }
    }
}
