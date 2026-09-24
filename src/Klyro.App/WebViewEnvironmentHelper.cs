using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Klyro_App;

/// <summary>
/// Centralized provider for Microsoft Edge WebView2 environments and configurations.
/// Ensures consistent options, eliminates environment clashes, and applies modern desktop User-Agents.
/// </summary>
public static class WebViewEnvironmentHelper
{
    public const string ChromeDesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Creates standardized options used for all exam and authentication WebView2 instances.
    /// </summary>
    public static CoreWebView2EnvironmentOptions CreateEnvironmentOptions()
    {
        return new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--process-per-tab --disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-blink-features=AutomationControlled"
        };
    }

    /// <summary>
    /// Gets or asynchronously creates the shared CoreWebView2Environment for the persistent Exam profile.
    /// Guarantees that LaunchPage (Screen 3 Google Sign-In) and MainPage (ExamWebView) share the identical runtime context.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetOrCreateSharedEnvironmentAsync()
    {
        if (_sharedEnvironment != null)
        {
            return _sharedEnvironment;
        }

        await _initLock.WaitAsync();
        try
        {
            if (_sharedEnvironment != null)
            {
                return _sharedEnvironment;
            }

            var profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Klyro", "Profiles", "Exam");
            Directory.CreateDirectory(profileDir);

            var options = CreateEnvironmentOptions();
            _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profileDir, options);
            return _sharedEnvironment;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Configures CoreWebView2Settings with the desktop User-Agent and examination security flags.
    /// </summary>
    public static void ConfigureStandardSettings(CoreWebView2Settings settings, bool allowDevTools = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.UserAgent = ChromeDesktopUserAgent;
        settings.AreDevToolsEnabled = allowDevTools;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
    }
}
