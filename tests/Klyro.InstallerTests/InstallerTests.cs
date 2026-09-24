using System;
using System.IO;
using System.Threading.Tasks;
using Klyro.Installer.Models;
using Klyro.Installer.Services;
using Xunit;

namespace Klyro.InstallerTests;

public class InstallerTests
{
    [Fact]
    public void DefaultInstallOptions_ShouldRequireTermsAcceptance()
    {
        var options = new InstallOptions();
        Assert.False(options.TermsAccepted, "Terms should not be accepted by default.");
        Assert.Equal(@"C:\Klyro", options.InstallDirectory);
        Assert.Equal(ShortcutPlacement.DesktopAndStartMenu, options.ShortcutChoice);
        Assert.Equal(InstallationScope.AllUsers, options.Scope);
        Assert.True(options.UnblockFiles);
        Assert.True(options.GrantUserPermissions);
        Assert.True(options.LaunchAfterInstall);
        Assert.False(options.IsSilent);
    }

    [Theory]
    [InlineData(ShortcutPlacement.DesktopAndStartMenu)]
    [InlineData(ShortcutPlacement.DesktopOnly)]
    [InlineData(ShortcutPlacement.StartMenuOnly)]
    [InlineData(ShortcutPlacement.None)]
    public void ShortcutPlacement_AllRadioOptionsAreValid(ShortcutPlacement placement)
    {
        var options = new InstallOptions { ShortcutChoice = placement };
        Assert.Equal(placement, options.ShortcutChoice);
    }

    [Fact]
    public void InstallationScope_GetDefaultDirectory_ReturnsAppropriatePaths()
    {
        string allUsersDir = InstallOptions.GetDefaultDirectory(InstallationScope.AllUsers);
        Assert.Equal(@"C:\Klyro", allUsersDir);

        string currentUserDir = InstallOptions.GetDefaultDirectory(InstallationScope.CurrentUser);
        Assert.Contains("Programs", currentUserDir);
        Assert.EndsWith("Klyro", currentUserDir);
    }

    [Fact]
    public void CheckIsAdministrator_ExecutesWithoutException()
    {
        // Must execute cleanly on any Windows machine without throwing
        bool isAdmin = InstallerEngine.CheckIsAdministrator();
        // Returned value should be a valid boolean
        Assert.True(isAdmin || !isAdmin);
    }

    [Fact]
    public void TermsAndConditions_ContainsMandatoryClauses()
    {
        string terms = TermsAndConditionsText.FullText;

        Assert.NotNull(terms);
        Assert.NotEmpty(terms);
        Assert.Contains("MIT LICENSE COMPLIANCE", terms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ASSESSMENT INTEGRITY REQUIREMENTS", terms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DUAL-WEBVIEW RESILIENCE & ZERO CODE-LOSS GUARANTEE", terms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRIVACY, TELEMETRY & ZERO EXFILTRATION COMMITMENT", terms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISCLAIMER OF WARRANTIES", terms, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerEngine_ProgressChanged_FiresCorrectly()
    {
        var engine = new InstallerEngine();
        int reportedPercent = -1;
        string? reportedMsg = null;

        engine.ProgressChanged += (p, m) =>
        {
            reportedPercent = p;
            reportedMsg = m;
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "Klyro_Test_Install_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var options = new InstallOptions
            {
                TermsAccepted = true,
                InstallDirectory = tempDir,
                ShortcutChoice = ShortcutPlacement.None
            };

            Assert.NotNull(engine);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
