using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Klyro.Core.Startup;
using Xunit;

namespace Klyro.UnitTests;

public class StartupPipelineTests : IDisposable
{
    private readonly string _tempDir;

    public StartupPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Klyro_Test_Startup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // Create sample config in config/default.json
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var configJson = @"{
          ""application"": { ""name"": ""Klyro"", ""version"": ""1.0.0"" },
          ""exam"": { ""startUrl"": ""https://www.codechef.com/"" },
          ""network"": { ""portalUrl"": ""http://172.16.103.254:8090/"" },
          ""security"": { ""exitPin"": ""270905"" },
          ""browser"": { ""allowedOrigins"": [""https://www.codechef.com""] }
        }";
        File.WriteAllText(Path.Combine(configDir, "default.json"), configJson);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task RunAsync_ReportsProgressSequentially()
    {
        var pipeline = new StartupPipeline(_tempDir);
        var progressList = new List<StartupProgress>();
        var progressHandler = new Progress<StartupProgress>(p => progressList.Add(p));

        var result = await pipeline.RunAsync(progressHandler);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotEmpty(progressList);
        Assert.Contains(progressList, p => p.Percentage >= 100);
        Assert.NotNull(result.Configuration);
        Assert.NotNull(result.AuditLogger);
    }
}
