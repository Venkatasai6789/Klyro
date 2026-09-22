using Klyro.Core.Browser;
using Xunit;

namespace Klyro.SecurityTests;

public class ProtocolGuardTests
{
    private readonly ProtocolGuard _guard = new();

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:student@university.edu")]
    [InlineData("tel:+1234567890")]
    [InlineData("sms:+1234567890")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-settings:network-wifi")]
    [InlineData("ms-windows-store://home")]
    [InlineData("powershell:Start-Process calc")]
    [InlineData("cmd:dir")]
    [InlineData("shell:AppsFolder")]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,<html><body>Hacked</body></html>")]
    [InlineData("view-source:https://www.codechef.com")]
    public void BlocksDangerousAndExternalProtocols(string url)
    {
        var decision = _guard.EvaluateProtocol(url);
        Assert.False(decision.IsAllowed, $"Protocol '{url}' should have been blocked!");
        Assert.Contains("blocked by security policy", decision.Reason);
    }

    [Theory]
    [InlineData("https://www.codechef.com/")]
    [InlineData("http://172.16.103.254:8090/")]
    [InlineData("about:blank")]
    public void AllowsValidWebAndBlankProtocols(string url)
    {
        var decision = _guard.EvaluateProtocol(url);
        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("invalid-uri-without-scheme")]
    public void BlocksInvalidAndEmptyProtocols(string? url)
    {
        var decision = _guard.EvaluateProtocol(url);
        Assert.False(decision.IsAllowed);
    }
}
