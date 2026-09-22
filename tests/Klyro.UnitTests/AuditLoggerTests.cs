using System;
using System.IO;
using System.Threading.Tasks;
using Klyro.Core.Logging;
using Xunit;

namespace Klyro.UnitTests;

public class AuditLoggerTests : IAsyncDisposable
{
    private readonly string _testLogDir;
    private readonly AuditLogger _logger;

    public AuditLoggerTests()
    {
        _testLogDir = Path.Combine(Path.GetTempPath(), "Klyro_LogTests_" + Guid.NewGuid().ToString("N"));
        _logger = new AuditLogger(_testLogDir);
    }

    [Fact]
    public async Task Log_WritesStructuredJsonLinesToFile()
    {
        _logger.Log(EventType.EXAM_STARTED, "ACTIVE", "Contest started successfully.", "https://www.codechef.com/", "EXAM-101");

        // Dispose flushes writer queue
        await _logger.DisposeAsync();

        Assert.True(File.Exists(_logger.LogFilePath));
        var content = await File.ReadAllTextAsync(_logger.LogFilePath);

        Assert.Contains("EXAM_STARTED", content);
        Assert.Contains("EXAM-101", content);
        Assert.Contains("Contest started successfully", content);
        Assert.Contains("https://www.codechef.com/", content);
    }

    [Fact]
    public async Task Log_ScrubsCredentialsFromDetails()
    {
        // Student inadvertently inputs a password into a detail field or query string
        _logger.Log(EventType.SECURITY_VIOLATION, "ACTIVE", "Attempted request with password=SecretPassword123 and token=xyz987token");

        await _logger.DisposeAsync();

        var content = await File.ReadAllTextAsync(_logger.LogFilePath);

        Assert.DoesNotContain("SecretPassword123", content);
        Assert.DoesNotContain("xyz987token", content);
        Assert.Contains("password=[REDACTED]", content);
        Assert.Contains("token=[REDACTED]", content);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _logger.DisposeAsync();
            if (Directory.Exists(_testLogDir))
            {
                Directory.Delete(_testLogDir, recursive: true);
            }
        }
        catch { }
    }
}
