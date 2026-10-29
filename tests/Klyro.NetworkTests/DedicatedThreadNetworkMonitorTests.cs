using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Network;
using Xunit;

namespace Klyro.NetworkTests;

public class DedicatedThreadNetworkMonitorTests
{
    private sealed class FastMockProbe : IConnectivityProbe
    {
        public Task<ProbeResult> ExecuteAsync(ProbeType probeType, string targetEndpoint, int timeoutMs, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ProbeResult.Success(probeType, targetEndpoint, 204, 15));
        }
    }

    [Fact]
    public void NetworkMonitor_StartsDedicatedPrioritizedOSThread()
    {
        var config = new KlyroConfiguration();
        config.Network.ProbeIntervalMs = 500;
        using var monitor = new NetworkMonitor(config, new FastMockProbe());

        monitor.Start();

        var workerThread = monitor.WorkerThread;
        Assert.NotNull(workerThread);
        Assert.Equal("Klyro_NetworkMonitor_Engine", workerThread.Name);
        Assert.True(workerThread.IsBackground);
        Assert.Equal(ThreadPriority.AboveNormal, workerThread.Priority);

        monitor.Stop();
    }

    [Fact]
    public async Task NetworkMonitor_DedicatedThreadTicksRapidly()
    {
        var config = new KlyroConfiguration();
        config.Network.ProbeIntervalMs = 500;
        using var monitor = new NetworkMonitor(config, new FastMockProbe());

        monitor.Start();
        await Task.Delay(250);

        long ticks = monitor.TotalTickCount;
        Assert.True(ticks >= 3, $"Expected ticks >= 3 in 250ms, got {ticks}");

        monitor.Stop();
    }

    [Fact]
    public async Task NetworkMonitor_ConcurrentLockFreeReaders_ExecuteWithoutDeadlockOrContention()
    {
        var config = new KlyroConfiguration();
        config.Network.ProbeIntervalMs = 100;
        using var monitor = new NetworkMonitor(config, new FastMockProbe());

        monitor.Start();

        const int readerCount = 8;
        const int iterationsPerReader = 1000;
        var readerTasks = new List<Task>();

        for (int i = 0; i < readerCount; i++)
        {
            readerTasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < iterationsPerReader; j++)
                {
                    var state = monitor.CurrentState;
                    var latency = monitor.LastLatencyMs;
                    var tier = monitor.ConnectionSpeedTier;
                    var matrix = monitor.LatestMatrix;

                    Assert.NotNull(tier);
                    Assert.NotNull(matrix);
                }
            }));
        }

        await Task.WhenAll(readerTasks);
        monitor.Stop();
    }
}
