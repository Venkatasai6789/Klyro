using System;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Persistence;
using Klyro.Core.Tabs;
using Xunit;

namespace Klyro.NetworkTests;

public class TabWorkerTests
{
    private sealed class InMemorySessionStore : ISessionStore
    {
        public SessionRecord? SavedRecord { get; private set; }

        public void SaveSession(SessionRecord record) => SavedRecord = record;
        public Task SaveSessionAsync(SessionRecord record) { SavedRecord = record; return Task.CompletedTask; }
        public SessionRecord? LoadActiveSession() => SavedRecord;
        public Task<SessionRecord?> LoadActiveSessionAsync() => Task.FromResult(SavedRecord);
        public bool HasActiveInterruptedSession() => SavedRecord != null && !SavedRecord.IsCompleted;
        public void ClearSession() => SavedRecord = null;
    }

    [Fact]
    public async Task AssessmentTabWorker_ExecutesOnDedicatedThread_AndSavesSessionAsynchronously()
    {
        var config = new KlyroConfiguration();
        var sessionStore = new InMemorySessionStore();
        using var worker = new AssessmentTabWorker(config, sessionStore);

        worker.Start();

        var workerThread = worker.WorkerThread;
        Assert.NotNull(workerThread);
        Assert.Equal("Klyro_Tab_Assessment", workerThread.Name);
        Assert.True(worker.IsRunning);

        var testRecord = new SessionRecord
        {
            SessionId = "test-session-123",
            ExamId = "exam-456",
            LastKnownAssessmentUrl = "https://www.codechef.com/test",
            IsCompleted = false
        };

        worker.QueueSessionSnapshot(testRecord);
        await Task.Delay(200);

        Assert.NotNull(sessionStore.SavedRecord);
        Assert.Equal("test-session-123", sessionStore.SavedRecord.SessionId);

        worker.Stop();
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public void WifiPortalTabWorker_ExecutesOnDedicatedThread_AndTogglesRecoveryMode()
    {
        var config = new KlyroConfiguration();
        using var worker = new WifiPortalTabWorker(config);

        worker.Start();

        var workerThread = worker.WorkerThread;
        Assert.NotNull(workerThread);
        Assert.Equal("Klyro_Tab_WifiPortal", workerThread.Name);
        Assert.True(worker.IsRunning);

        worker.SetRecoveryMode(true);
        worker.SetRecoveryMode(false);

        worker.Stop();
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public void TabWorkerManager_StartsAndStopsAllWorkersGracefully()
    {
        var config = new KlyroConfiguration();
        using var manager = new TabWorkerManager(config);

        Assert.Equal(2, manager.Workers.Count);

        manager.StartAll();
        Assert.True(manager.AssessmentWorker.IsRunning);
        Assert.True(manager.WifiPortalWorker.IsRunning);

        manager.StopAll();
        Assert.False(manager.AssessmentWorker.IsRunning);
        Assert.False(manager.WifiPortalWorker.IsRunning);
    }
}
