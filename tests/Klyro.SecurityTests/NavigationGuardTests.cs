using Klyro.Core.Browser;
using Klyro.Core.Configuration;
using Klyro.Core.Security;
using Xunit;

namespace Klyro.SecurityTests;

public class NavigationGuardTests
{
    private readonly NavigationGuard _guard;
    private readonly KlyroConfiguration _config;

    public NavigationGuardTests()
    {
        _config = new KlyroConfiguration
        {
            Application = new ApplicationOptions { Environment = "Production" },
            Browser = new BrowserOptions
            {
                AllowedOrigins = new()
                {
                    "https://www.codechef.com",
                    "https://cdn.codechef.com",
                    "https://campus.codechef.com"
                },
                RecoveryAllowedOrigins = new()
                {
                    "http://172.16.103.254:8090"
                }
            }
        };

        var allowlistManager = new AllowlistManager(_config);
        _guard = new NavigationGuard(allowlistManager);
    }

    [Fact]
    public void AllowsApprovedCodeChefOrigin()
    {
        var decision = _guard.EvaluateExamNavigation("https://www.codechef.com/");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void AllowsApprovedCodeChefContestSubpath()
    {
        var decision = _guard.EvaluateExamNavigation("https://www.codechef.com/START142/problems/PROB1");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void AllowsApprovedCdnOrigin()
    {
        var decision = _guard.EvaluateExamNavigation("https://cdn.codechef.com/monaco-editor/bundle.js");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void AllowsAboutBlankForInitialization()
    {
        var decision = _guard.EvaluateExamNavigation("about:blank");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void BlocksExternalDomain_Google()
    {
        var decision = _guard.EvaluateExamNavigation("https://www.google.com");
        Assert.False(decision.IsAllowed);
        Assert.Contains("not permitted", decision.Reason);
    }

    [Fact]
    public void BlocksExternalDomain_GitHub()
    {
        var decision = _guard.EvaluateExamNavigation("https://github.com/login");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void BlocksLookalikeDomain_HyphenatedPrefix()
    {
        // Attack: evil-codechef.com
        var decision = _guard.EvaluateExamNavigation("https://evil-codechef.com/login");
        Assert.False(decision.IsAllowed);
        Assert.Contains("not permitted", decision.Reason);
    }

    [Fact]
    public void BlocksLookalikeDomain_SuffixTldSpoofing()
    {
        // Attack: codechef.com.evil.com
        var decision = _guard.EvaluateExamNavigation("https://www.codechef.com.attacker.org/contest");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void BlocksUnapprovedSubdomainOfCodeChef()
    {
        // Subdomains not explicitly in allowlist must fail closed
        var decision = _guard.EvaluateExamNavigation("https://internal-dev.codechef.com/login");
        Assert.False(decision.IsAllowed);
        Assert.Contains("Explicit subdomain whitelisting is required", decision.Reason);
    }

    [Fact]
    public void BlocksInsecureHttpInProductionExam()
    {
        // CodeChef over unencrypted HTTP must be blocked in production
        var decision = _guard.EvaluateExamNavigation("http://www.codechef.com/");
        Assert.False(decision.IsAllowed);
        Assert.Contains("Insecure HTTP scheme is prohibited", decision.Reason);
    }

    [Fact]
    public void BlocksEmptyAndMalformedUrls()
    {
        Assert.False(_guard.EvaluateExamNavigation("").IsAllowed);
        Assert.False(_guard.EvaluateExamNavigation("   ").IsAllowed);
        Assert.False(_guard.EvaluateExamNavigation(null).IsAllowed);
        Assert.False(_guard.EvaluateExamNavigation("not-a-valid-uri").IsAllowed);
    }

    [Fact]
    public void AllowsApprovedRecoveryCaptivePortal()
    {
        var decision = _guard.EvaluateRecoveryNavigation("http://172.16.103.254:8090/");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void BlocksExternalWebsiteInsideRecovery()
    {
        var decision = _guard.EvaluateRecoveryNavigation("https://www.google.com");
        Assert.False(decision.IsAllowed);
    }
}
