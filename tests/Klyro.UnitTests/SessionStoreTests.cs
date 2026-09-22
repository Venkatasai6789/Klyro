using System;
using System.IO;
using Klyro.Core.Exam;
using Klyro.Core.Persistence;
using Xunit;

namespace Klyro.UnitTests;

public class SessionStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Klyro_Tests_" + Guid.NewGuid().ToString("N"));
        _store = new SessionStore(_testDir);
    }

    [Fact]
    public void SaveAndLoadSession_PreservesAllFields()
    {
        var record = new SessionRecord
        {
            ExamId = "COOK142",
            SessionId = Guid.NewGuid().ToString(),
            MachineId = "LAB-05",
            CurrentExamState = ExamState.ACTIVE,
            LastKnownAssessmentUrl = "https://www.codechef.com/COOK142",
            RemainingSeconds = 5400,
            IsCompleted = false
        };

        _store.SaveSession(record);

        var loaded = _store.LoadActiveSession();
        Assert.NotNull(loaded);
        Assert.Equal(record.ExamId, loaded.ExamId);
        Assert.Equal(record.SessionId, loaded.SessionId);
        Assert.Equal(record.MachineId, loaded.MachineId);
        Assert.Equal(ExamState.ACTIVE, loaded.CurrentExamState);
        Assert.Equal("https://www.codechef.com/COOK142", loaded.LastKnownAssessmentUrl);
        Assert.Equal(5400, loaded.RemainingSeconds);
        Assert.False(loaded.IsCompleted);
    }

    [Fact]
    public void HasActiveInterruptedSession_TrueWhenIncomplete()
    {
        var record = new SessionRecord
        {
            ExamId = "COOK142",
            CurrentExamState = ExamState.ACTIVE,
            IsCompleted = false
        };

        _store.SaveSession(record);
        Assert.True(_store.HasActiveInterruptedSession());
    }

    [Fact]
    public void HasActiveInterruptedSession_FalseWhenCompleted()
    {
        var record = new SessionRecord
        {
            ExamId = "COOK142",
            CurrentExamState = ExamState.COMPLETED,
            IsCompleted = true
        };

        _store.SaveSession(record);
        Assert.False(_store.HasActiveInterruptedSession());
        Assert.Null(_store.LoadActiveSession());
    }

    [Fact]
    public void ClearSession_RemovesPersistenceFile()
    {
        var record = new SessionRecord { ExamId = "COOK142" };
        _store.SaveSession(record);

        Assert.True(_store.HasActiveInterruptedSession());

        _store.ClearSession();
        Assert.False(_store.HasActiveInterruptedSession());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }
}
