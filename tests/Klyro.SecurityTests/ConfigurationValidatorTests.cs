using Klyro.Core.Configuration;
using Xunit;

namespace Klyro.SecurityTests;

public class ConfigurationValidatorTests
{
    private KlyroConfiguration CreateValidConfig()
    {
        return new KlyroConfiguration
        {
            Application = new ApplicationOptions
            {
                Name = "Klyro",
                Version = "1.0.0",
                Environment = "Production"
            },
            Exam = new ExamOptions
            {
                Provider = "CodeChef",
                StartUrl = "https://www.codechef.com/",
                TimerPolicy = "PauseDuringVerifiedNetworkFailure",
                DurationMinutes = 120
            },
            Network = new NetworkOptions
            {
                PortalUrl = "http://172.16.103.254:8090/",
                ProbeIntervalMs = 3000,
                ProbeTimeoutMs = 1500,
                FailureThreshold = 3,
                RecoveryTimeoutMs = 60000
            },
            Security = new SecurityOptions
            {
                AllowDevTools = false,
                AllowDownloads = false
            },
            Browser = new BrowserOptions
            {
                AllowedOrigins = new() { "https://www.codechef.com" },
                RecoveryAllowedOrigins = new() { "http://172.16.103.254:8090" }
            }
        };
    }

    [Fact]
    public void ValidConfig_PassesValidation()
    {
        var config = CreateValidConfig();
        var result = ConfigurationValidator.Validate(config);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://*")]
    [InlineData("https://*")]
    [InlineData("https://*.codechef.com")]
    [InlineData("*://*")]
    public void RejectsDangerousWildcardsInAllowlist(string wildcard)
    {
        var config = CreateValidConfig();
        config.Browser.AllowedOrigins.Add(wildcard);

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("wildcard", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsHttpExamUrlInProduction()
    {
        var config = CreateValidConfig();
        config.Exam.StartUrl = "http://www.codechef.com/";

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must use HTTPS in production", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsDevToolsInProduction()
    {
        var config = CreateValidConfig();
        config.Security.AllowDevTools = true;

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("AllowDevTools cannot be enabled in a Production environment"));
    }

    [Fact]
    public void RejectsInvalidTimerPolicy()
    {
        var config = CreateValidConfig();
        config.Exam.TimerPolicy = "ArbitraryPolicy";

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid Exam.TimerPolicy"));
    }

    [Fact]
    public void RejectsProbeTimeoutGreaterThanInterval()
    {
        var config = CreateValidConfig();
        config.Network.ProbeIntervalMs = 2000;
        config.Network.ProbeTimeoutMs = 2500; // Greater than interval!

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("less than ProbeIntervalMs"));
    }

    [Fact]
    public void RejectsMalformedPortalUrl()
    {
        var config = CreateValidConfig();
        config.Network.PortalUrl = "not-a-valid-url";

        var result = ConfigurationValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Network.PortalUrl is not a valid absolute URI"));
    }
}
