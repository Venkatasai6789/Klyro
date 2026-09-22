using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;

namespace Klyro.Core.Network;

/// <summary>
/// Autonomous background service that continuously polls multi-probe network signals.
/// </summary>
public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly KlyroConfiguration _config;
    private readonly IConnectivityProbe _probe;
    private readonly IAuditLogger? _auditLogger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private bool _isDisposed;

    public NetworkStateMachine StateMachine { get; }
    public INetworkSimulator Simulator { get; } = new NetworkSimulator();

    public NetworkState CurrentState => StateMachine.CurrentState;
    public long LastLatencyMs { get; private set; }
    public string ConnectionSpeedTier { get; private set; } = "Unknown";

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
        if (_monitorTask != null) return;
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public async Task<NetworkState> TriggerImmediateProbeAsync(CancellationToken cancellationToken = default)
    {
        var matrix = await ExecuteAllProbesAsync(cancellationToken);
        if (matrix.Internet.IsSuccess)
        {
            LastLatencyMs = matrix.Internet.LatencyMs;
            ConnectionSpeedTier = LastLatencyMs switch
            {
                < 80 => "Fast",
                < 250 => "Good",
                _ => "Slow"
            };
        }
        else
        {
            ConnectionSpeedTier = "Offline";
        }

        var state = StateMachine.ProcessProbeMatrix(matrix);
        ProbeCompleted?.Invoke(this, matrix);
        return state;
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await TriggerImmediateProbeAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(
                    EventType.NETWORK_DEGRADED,
                    StateMachine.CurrentState.ToString(),
                    $"Network monitor loop exception: {ex.Message}");
            }

            try
            {
                await Task.Delay(_config.Network.ProbeIntervalMs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
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
        var gatewayTask = ProbeGatewayAsync(gatewayEndpoint, timeout, cancellationToken);
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
        _cts.Dispose();

        if (_probe is IDisposable disposableProbe)
        {
            disposableProbe.Dispose();
        }
    }
}
