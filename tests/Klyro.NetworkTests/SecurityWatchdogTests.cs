using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Security;
using Xunit;

namespace Klyro.NetworkTests;

public class SecurityWatchdogTests
{
    [Fact]
    public void SecurityWatchdog_StartsDedicatedBelowNormalOSThread()
    {
        var options = new SecurityOptions();
        using var watchdog = new SecurityWatchdog(options);

        watchdog.Start();

        var thread = watchdog.WorkerThread;
        Assert.NotNull(thread);
        Assert.Equal("Klyro_Security_Watchdog", thread.Name);
        Assert.True(thread.IsBackground);
        Assert.Equal(ThreadPriority.BelowNormal, thread.Priority);
        Assert.True(watchdog.IsRunning);

        watchdog.Stop();
        Assert.False(watchdog.IsRunning);
    }
}
