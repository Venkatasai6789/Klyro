using Klyro.Core.Browser;
using Klyro.Core.Configuration;
using Klyro.Core.Security;
using Xunit;

namespace Klyro.RecoveryTests;

public class RecoveryAllowlistTests
{
    private readonly NavigationGuard _navigationGuard;

    public RecoveryAllowlistTests()
    {
        var config = new KlyroConfiguration
        {
            Browser = new BrowserOptions
            {
                AllowedOrigins = new() { "https://www.codechef.com" },
                RecoveryAllowedOrigins = new() { "http://172.16.103.254:8090" }
            }
        };

        var allowlist = new AllowlistManager(config);
        _navigationGuard = new NavigationGuard(allowlist);
    }

    [Theory]
    [InlineData("http://172.16.103.254:8090/")]
    [InlineData("http://172.16.103.254:8090/login")]
    [InlineData("http://172.16.103.254:8090/auth.do?action=login")]
    [InlineData("http://172.16.103.254:8090/static/style.css")]
    public void RecoveryAllowlist_AllowsApprovedPortalOriginsAndSubpaths(string url)
    {
        var decision = _navigationGuard.EvaluateRecoveryNavigation(url);
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Theory]
    [InlineData("http://172.16.103.254:8080/")] // Wrong port
    [InlineData("http://172.16.103.253:8090/")] // Wrong IP
    [InlineData("https://www.google.com")]
    [InlineData("https://www.youtube.com")]
    [InlineData("https://github.com")]
    [InlineData("https://chatgpt.com")]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("javascript:alert(1)")]
    public void RecoveryAllowlist_BlocksUnapprovedOriginsAndProtocols(string url)
    {
        var decision = _navigationGuard.EvaluateRecoveryNavigation(url);
        Assert.False(decision.IsAllowed, $"URL '{url}' must be blocked in recovery mode!");
    }
}
