using System;
using System.IO;
using Klyro.Core.Configuration;
using Xunit;

namespace Klyro.UnitTests;

public class ConfigurationLoaderTests
{
    private static string FindRepositoryRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Klyro.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate repository root containing Klyro.sln.");
    }

    [Fact]
    public void LoadDefaultJson_SucceedsAndValidates()
    {
        var repoRoot = FindRepositoryRoot();
        var defaultJsonPath = Path.Combine(repoRoot, "config", "default.json");

        Assert.True(File.Exists(defaultJsonPath), $"File not found: {defaultJsonPath}");
        var config = ConfigurationLoader.LoadFromFile(defaultJsonPath);

        Assert.NotNull(config);
        Assert.Equal("Klyro", config.Application.Name);
        Assert.Equal("CodeChef", config.Exam.Provider);
        Assert.Equal("http://172.16.103.254:8090/", config.Network.PortalUrl);
        Assert.False(config.Security.AllowDevTools);
        Assert.Contains("https://www.codechef.com", config.Browser.AllowedOrigins);
    }

    [Fact]
    public void LoadDevelopmentJson_SucceedsAndValidates()
    {
        var repoRoot = FindRepositoryRoot();
        var devJsonPath = Path.Combine(repoRoot, "config", "development.json");

        Assert.True(File.Exists(devJsonPath), $"File not found: {devJsonPath}");
        var config = ConfigurationLoader.LoadFromFile(devJsonPath);

        Assert.NotNull(config);
        Assert.Equal("Development", config.Application.Environment);
        Assert.True(config.Network.EnableNetworkSimulator);
    }

    [Fact]
    public void LoadExamExampleJson_SucceedsAndValidates()
    {
        var repoRoot = FindRepositoryRoot();
        var examJsonPath = Path.Combine(repoRoot, "config", "exam.example.json");

        Assert.True(File.Exists(examJsonPath), $"File not found: {examJsonPath}");
        var config = ConfigurationLoader.LoadFromFile(examJsonPath);

        Assert.NotNull(config);
        Assert.Equal("COOK142", config.Exam.ContestId);
    }

    [Fact]
    public void LoadMissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ConfigurationLoader.LoadFromFile("non_existent_config.json"));
    }

    [Fact]
    public void LoadMalformedJson_ThrowsInvalidOperationException()
    {
        var badJson = "{ \"application\": { \"name\": \"broken\" "; // missing closing brace
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationLoader.LoadFromJson(badJson, "bad.json"));
    }
}
