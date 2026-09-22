using Klyro.Core.Browser;
using Klyro.Core.Configuration;
using Klyro.Core.Security;
using Xunit;

namespace Klyro.SecurityTests;

public class PopupAndDownloadGuardTests
{
    [Fact]
    public void PopupGuard_DefaultPolicy_BlocksAllPopups()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowNewWindows = false, AllowNewTabs = false },
            Browser = new BrowserOptions { AllowedOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var popupGuard = new PopupGuard(allowlist, config.Security);

        var decision = popupGuard.EvaluateNewWindowRequest("https://www.codechef.com/popup");
        Assert.False(decision.IsAllowed);
        Assert.Contains("disabled by examination security policy", decision.Reason);
    }

    [Fact]
    public void PopupGuard_EnabledPolicy_BlocksUnauthorizedOrigins()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowNewWindows = true, AllowNewTabs = false },
            Browser = new BrowserOptions { AllowedOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var popupGuard = new PopupGuard(allowlist, config.Security);

        var decision = popupGuard.EvaluateNewWindowRequest("https://www.google.com");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void PopupGuard_EnabledPolicy_AllowsAuthorizedOrigin()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowNewWindows = true, AllowNewTabs = false },
            Browser = new BrowserOptions { AllowedOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var popupGuard = new PopupGuard(allowlist, config.Security);

        var decision = popupGuard.EvaluateNewWindowRequest("https://www.codechef.com/docs/help");
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public void DownloadGuard_DefaultPolicy_BlocksDownloads()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowDownloads = false },
            Browser = new BrowserOptions { ApprovedDownloadOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var downloadGuard = new DownloadGuard(allowlist, config.Security);

        var decision = downloadGuard.EvaluateDownloadRequest("https://www.codechef.com/downloads/problem.pdf");
        Assert.False(decision.IsAllowed);
        Assert.Contains("prohibited by examination security policy", decision.Reason);
    }

    [Fact]
    public void DownloadGuard_EnabledPolicy_BlocksUnapprovedDownloadOrigins()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowDownloads = true },
            Browser = new BrowserOptions { ApprovedDownloadOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var downloadGuard = new DownloadGuard(allowlist, config.Security);

        var decision = downloadGuard.EvaluateDownloadRequest("https://malicious-files.com/trojan.exe");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void DownloadGuard_EnabledPolicy_AllowsApprovedDownloadOrigin()
    {
        var config = new KlyroConfiguration
        {
            Security = new SecurityOptions { AllowDownloads = true },
            Browser = new BrowserOptions { ApprovedDownloadOrigins = new() { "https://www.codechef.com" } }
        };
        var allowlist = new AllowlistManager(config);
        var downloadGuard = new DownloadGuard(allowlist, config.Security);

        var decision = downloadGuard.EvaluateDownloadRequest("https://www.codechef.com/attachments/testcase.zip");
        Assert.True(decision.IsAllowed, decision.Reason);
    }
}
