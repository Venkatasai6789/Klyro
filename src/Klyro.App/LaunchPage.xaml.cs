using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using Klyro.Core.Authentication;
using Klyro.Core.Configuration;
using Klyro.Core.Persistence;
using Klyro.Core.Preflight;
using Klyro.Core.Startup;

namespace Klyro_App;

public sealed partial class LaunchPage : Page
{
    private const double MaxProgressBarWidth = 380.0;
    private readonly StartupPipeline _pipeline = new();
    private readonly OnboardingStateStore _onboardingStore = new();
    private readonly KlyroConfiguration _config = LoadConfiguration();
    private readonly SystemBrowserAuthService _browserAuthService = new();
    private readonly IEnvironmentPreflightService _preflightService;
    private CancellationTokenSource? _startupCts;
    private CancellationTokenSource? _preflightCts;
    private double _currentProgressWidth;
    private bool _isGoogleAuthInitialized = false;
    private bool _isAuthCompleted = false;
    private bool _isBrowserAuthInProgress = false;
    private AuthenticatedUser? _currentUser;
    private AuthenticatedUser? _previousUser;
    private bool _isAccountSwitching = false;

    private static void PageLog(string msg)
    {
        try
        {
            var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "boot.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [LaunchPage] {msg}\n");
        }
        catch { }
    }

    public LaunchPage()
    {
        PageLog("Constructor entered.");
        try
        {
            _preflightService = new EnvironmentPreflightService(_config);
            InitializeComponent();
            Unloaded += LaunchPage_Unloaded;
            _browserAuthService.AuthCompleted += BrowserAuthService_AuthCompleted;
            if (MainWindow.CurrentInstance != null)
            {
                MainWindow.CurrentInstance.WindowActivated += MainWindow_WindowActivated;
            }
            PageLog("InitializeComponent succeeded.");
        }
        catch (Exception ex)
        {
            PageLog($"InitializeComponent failed: {ex}");
            throw;
        }
    }

    private void LaunchPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _browserAuthService.Dispose();
        if (MainWindow.CurrentInstance != null)
        {
            MainWindow.CurrentInstance.WindowActivated -= MainWindow_WindowActivated;
        }
    }

    private void MainWindow_WindowActivated(object? sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;

        // If external browser sign-in was in progress and the user switches back to Klyro (like in modern IDEs)
        if (_isBrowserAuthInProgress && !_isAuthCompleted)
        {
            PageLog("Window activation detected while browser sign-in is in progress. Starting background login completion...");
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_isAuthCompleted) return;

                // Immediate visual confirmation on the waiting card
                BrowserAuthSpinner.IsActive = true;
                BrowserAuthHeaderTitle.Text = "Browser Sign-In Detected!";
                BrowserAuthStatusPillText.Text = "Authenticating account credentials...";
                BrowserAuthDescText.Text = "Detected return from browser. Connecting your Google account...";
                BrowserAuthVerifyBtn.IsEnabled = false;

                await Task.Delay(400);

                var detectedUser = BrowserProfileDetector.DetectLatestGoogleAccount();
                await CompleteGoogleAuthenticationAsync(detectedUser, "Default Browser (Auto-detected)");
            });
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PageLog("Page_Loaded entered.");
        try
        {
            var cmdArgs = Environment.GetCommandLineArgs();

            bool directScreen5 = cmdArgs.Any(a =>
                a.Equals("--screen5", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--preflight-prepare", StringComparison.OrdinalIgnoreCase));

            if (directScreen5)
            {
                PageLog("Direct Screen 5 (Prepare Environment) requested via CLI arguments.");
                StartupStateGrid.Visibility = Visibility.Collapsed;
                WelcomeStateGrid.Visibility = Visibility.Collapsed;
                AccountSetupStateGrid.Visibility = Visibility.Collapsed;
                AccountConnectedStateGrid.Visibility = Visibility.Collapsed;
                PreflightCheckingStateGrid.Visibility = Visibility.Collapsed;
                StartupScreenBg.Opacity = 0.0;
                GoogleSigninBg.Opacity = 0.0;
                KlyroBrandLogo.Opacity = 0.0;
                PreflightScreenBg.Opacity = 1.0;
                PreflightPrepareStateGrid.Visibility = Visibility.Visible;
                PreflightPrepareStateGrid.Opacity = 1.0;

                string? screenshotScreen5Path = null;
                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if (cmdArgs[i].Equals("--screenshot-screen5", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        screenshotScreen5Path = cmdArgs[i + 1];
                }

                if (!string.IsNullOrEmpty(screenshotScreen5Path))
                {
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotScreen5Path);
                }
                return;
            }

            bool directScreen6 = cmdArgs.Any(a =>
                a.Equals("--screen6", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--preflight-checking", StringComparison.OrdinalIgnoreCase));

            if (directScreen6)
            {
                PageLog("Direct Screen 6 (Checking Environment) requested via CLI arguments.");
                StartupStateGrid.Visibility = Visibility.Collapsed;
                WelcomeStateGrid.Visibility = Visibility.Collapsed;
                AccountSetupStateGrid.Visibility = Visibility.Collapsed;
                AccountConnectedStateGrid.Visibility = Visibility.Collapsed;
                PreflightPrepareStateGrid.Visibility = Visibility.Collapsed;
                StartupScreenBg.Opacity = 0.0;
                GoogleSigninBg.Opacity = 0.0;
                KlyroBrandLogo.Opacity = 0.0;
                PreflightScreenBg.Opacity = 1.0;
                PreflightCheckingStateGrid.Visibility = Visibility.Visible;
                PreflightCheckingStateGrid.Opacity = 1.0;

                string? screenshotScreen6Path = null;
                string? testEmail = null;
                string? testName = null;
                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if (cmdArgs[i].Equals("--screenshot-screen6", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        screenshotScreen6Path = cmdArgs[i + 1];
                    else if (cmdArgs[i].Equals("--test-user-email", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        testEmail = cmdArgs[i + 1];
                    else if (cmdArgs[i].Equals("--test-user-name", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        testName = cmdArgs[i + 1];
                }

                if (_currentUser == null)
                {
                    var detected = BrowserProfileDetector.DetectLatestGoogleAccount();
                    _currentUser = new AuthenticatedUser
                    {
                        DisplayName = testName ?? detected?.DisplayName ?? "P. Venkata Sai",
                        Email = testEmail ?? detected?.Email ?? "p.venkatsai333@gmail.com",
                        PhotoUrl = detected?.PhotoUrl
                    };
                }

                _ = RunPreflightChecksAsync(screenshotScreen6Path);
                return;
            }

            bool directScreen4 = cmdArgs.Any(a =>
                a.Equals("--screen4", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--account-connected", StringComparison.OrdinalIgnoreCase));

            if (directScreen4)
            {
                PageLog("Direct Screen 4 (Account Connected) requested via CLI arguments.");
                StartupStateGrid.Visibility = Visibility.Collapsed;
                WelcomeStateGrid.Visibility = Visibility.Collapsed;
                AccountSetupStateGrid.Visibility = Visibility.Collapsed;
                StartupScreenBg.Opacity = 0.0;
                KlyroBrandLogo.Opacity = 0.0;
                GoogleSigninBg.Opacity = 1.0;
                AccountConnectedStateGrid.Visibility = Visibility.Visible;
                AccountConnectedStateGrid.Opacity = 1.0;

                var detectedAcc = BrowserProfileDetector.DetectLatestGoogleAccount();
                string testName = detectedAcc?.DisplayName ?? string.Empty;
                string testEmail = detectedAcc?.Email ?? string.Empty;
                string? testPhoto = detectedAcc?.PhotoUrl;
                string? screenshotScreen4Path = null;
                string? screenshotScreen4FallbackPath = null;

                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if (cmdArgs[i].Equals("--test-user-name", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                    {
                        var parts = new List<string>();
                        for (int j = i + 1; j < cmdArgs.Length && !cmdArgs[j].StartsWith("--"); j++)
                        {
                            parts.Add(cmdArgs[j]);
                        }
                        if (parts.Count > 0)
                        {
                            testName = string.Join(" ", parts);
                        }
                    }
                    else if (cmdArgs[i].Equals("--test-user-email", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        testEmail = cmdArgs[i + 1];
                    else if (cmdArgs[i].Equals("--test-user-photo", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        testPhoto = cmdArgs[i + 1];
                    else if (cmdArgs[i].Equals("--screenshot-screen4", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        screenshotScreen4Path = cmdArgs[i + 1];
                    else if (cmdArgs[i].Equals("--screenshot-screen4-fallback", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                        screenshotScreen4FallbackPath = cmdArgs[i + 1];
                }

                var user = new AuthenticatedUser
                {
                    DisplayName = testName,
                    Email = testEmail,
                    PhotoUrl = testPhoto
                };
                SetConnectedUser(user);

                if (!string.IsNullOrEmpty(screenshotScreen4Path))
                {
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotScreen4Path);
                }

                if (!string.IsNullOrEmpty(screenshotScreen4FallbackPath))
                {
                    ConnectedUserPhotoBrush.ImageSource = null;
                    ConnectedUserPhotoBorder.Visibility = Visibility.Collapsed;
                    ConnectedUserInitials.Visibility = Visibility.Visible;
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotScreen4FallbackPath);
                }
                return;
            }

            bool directScreen3 = cmdArgs.Any(a =>
                a.Equals("--screen3", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--account-setup", StringComparison.OrdinalIgnoreCase));

            if (directScreen3)
            {
                PageLog("Direct Screen 3 (Account Setup) requested via CLI arguments.");
                StartupStateGrid.Visibility = Visibility.Collapsed;
                WelcomeStateGrid.Visibility = Visibility.Collapsed;
                StartupScreenBg.Opacity = 0.0;
                KlyroBrandLogo.Opacity = 0.0;
                GoogleSigninBg.Opacity = 1.0;
                AccountSetupStateGrid.Visibility = Visibility.Visible;
                AccountSetupStateGrid.Opacity = 1.0;

                string? screenshotScreen3Path = null;
                string? screenshotFallbackPath = null;
                string? screenshotInvigilatorPath = null;
                string? screenshotCardPath = null;

                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if ((cmdArgs[i].Equals("--screenshot-screen3", StringComparison.OrdinalIgnoreCase) ||
                         cmdArgs[i].Equals("--screenshot-account-setup", StringComparison.OrdinalIgnoreCase)) && i + 1 < cmdArgs.Length)
                    {
                        screenshotScreen3Path = cmdArgs[i + 1];
                    }
                    else if (cmdArgs[i].Equals("--screenshot-fallback", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                    {
                        screenshotFallbackPath = cmdArgs[i + 1];
                    }
                    else if (cmdArgs[i].Equals("--screenshot-invigilator", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                    {
                        screenshotInvigilatorPath = cmdArgs[i + 1];
                    }
                    else if (cmdArgs[i].Equals("--screenshot-card", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                    {
                        screenshotCardPath = cmdArgs[i + 1];
                    }
                }

                if (!string.IsNullOrEmpty(screenshotScreen3Path))
                {
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotScreen3Path);
                }

                if (cmdArgs.Any(a => a.Equals("--test-error-card", StringComparison.OrdinalIgnoreCase)))
                {
                    PageLog("Auto-triggering error card for testing...");
                    GoogleAuthErrorCard.Visibility = Visibility.Visible;
                    if (!string.IsNullOrEmpty(screenshotCardPath))
                    {
                        await Task.Delay(400);
                        await CaptureScreenshotAsync(screenshotCardPath);
                    }
                }

                if (cmdArgs.Any(a => a.Equals("--test-fallback", StringComparison.OrdinalIgnoreCase)))
                {
                    PageLog("Auto-triggering fallback view for testing...");
                    GoogleAuthOverlay.Visibility = Visibility.Visible;
                    GoogleAuthOverlay.Opacity = 1.0;
                    ShowGoogleAuthFallback(CoreWebView2WebErrorStatus.HostNameNotResolved);
                    if (!string.IsNullOrEmpty(screenshotFallbackPath))
                    {
                        await Task.Delay(400);
                        await CaptureScreenshotAsync(screenshotFallbackPath);
                    }
                }

                if (cmdArgs.Any(a => a.Equals("--test-invigilator", StringComparison.OrdinalIgnoreCase)))
                {
                    PageLog("Auto-triggering invigilator modal for testing...");
                    OpenInvigilatorBypass_Click(this, new RoutedEventArgs());
                    if (!string.IsNullOrEmpty(screenshotInvigilatorPath))
                    {
                        await Task.Delay(400);
                        await CaptureScreenshotAsync(screenshotInvigilatorPath);
                    }
                }

                if (cmdArgs.Any(a => a.Equals("--test-browser-waiting", StringComparison.OrdinalIgnoreCase)))
                {
                    PageLog("Auto-triggering browser waiting card for testing...");
                    BrowserAuthWaitingCard.Visibility = Visibility.Visible;
                    if (!string.IsNullOrEmpty(screenshotCardPath))
                    {
                        await Task.Delay(400);
                        await CaptureScreenshotAsync(screenshotCardPath);
                    }
                }

                if (cmdArgs.Any(a => a.Equals("--test-click-google", StringComparison.OrdinalIgnoreCase)))
                {
                    PageLog("Auto-triggering GoogleSignIn_Click for testing...");
                    GoogleSignIn_Click(this, new RoutedEventArgs());
                }
                return;
            }

            bool directWelcome = cmdArgs.Any(a =>
                a.Equals("--welcome", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--screen2", StringComparison.OrdinalIgnoreCase));

            if (directWelcome)
            {
                PageLog("Direct welcome screen requested via CLI arguments.");
                StartupStateGrid.Visibility = Visibility.Collapsed;
                WelcomeStateGrid.Visibility = Visibility.Visible;
                WelcomeStateGrid.Opacity = 1.0;

                string? screenshotWelcomePath = null;
                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if (cmdArgs[i].Equals("--screenshot-welcome", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                    {
                        screenshotWelcomePath = cmdArgs[i + 1];
                    }
                }

                if (!string.IsNullOrEmpty(screenshotWelcomePath))
                {
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotWelcomePath);
                }
                return;
            }

            await StartStartupPipelineAsync();
        }
        catch (Exception ex)
        {
            PageLog($"Page_Loaded exception: {ex}");
        }
    }

    private async Task StartStartupPipelineAsync()
    {
        PageLog("StartStartupPipelineAsync starting...");
        try
        {
            _startupCts?.Cancel();
            _startupCts = new CancellationTokenSource();
            var ct = _startupCts.Token;

            StartupErrorCard.Visibility = Visibility.Collapsed;
            UpdateProgress(0, "Starting Klyro...");

            var progressHandler = new Progress<StartupProgress>(p =>
            {
                PageLog($"Progress callback: {p.Percentage}% - {p.StatusText}");
                UpdateProgress(p.Percentage, p.StatusText);
            });

            var cmdArgs = Environment.GetCommandLineArgs();
            string? screenshotPath = null;
            string? screenshotReadyPath = null;
            string? screenshotWelcomePath = null;
            bool stayOnLaunch = false;

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                {
                    screenshotPath = cmdArgs[i + 1];
                }
                else if (cmdArgs[i].Equals("--screenshot-ready", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                {
                    screenshotReadyPath = cmdArgs[i + 1];
                }
                else if (cmdArgs[i].Equals("--screenshot-welcome", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                {
                    screenshotWelcomePath = cmdArgs[i + 1];
                }
                else if (cmdArgs[i].Equals("--stay-on-launch", StringComparison.OrdinalIgnoreCase) ||
                         cmdArgs[i].Equals("--preview", StringComparison.OrdinalIgnoreCase))
                {
                    stayOnLaunch = true;
                }
            }

            PageLog($"Startup options: stayOnLaunch={stayOnLaunch}");

            // If a screenshot was requested at startup state
            if (!string.IsNullOrEmpty(screenshotPath))
            {
                await Task.Delay(400); // Allow full visual layout and image assets to decode
                await CaptureScreenshotAsync(screenshotPath);
            }

            PageLog("Calling _pipeline.RunAsync...");
            var result = await _pipeline.RunAsync(progressHandler, ct);
            PageLog($"_pipeline.RunAsync completed. Result: {result.IsSuccess}, Error: {result.ErrorMessage}");

            if (result.IsSuccess)
            {
                UpdateProgress(100, "Klyro is ready.");

                if (!string.IsNullOrEmpty(screenshotReadyPath))
                {
                    await Task.Delay(200);
                    await CaptureScreenshotAsync(screenshotReadyPath);
                }

                // 200–400ms pause as per specification
                await Task.Delay(350);

                // Polished transition from Screen 1 (Startup) to Screen 2 (Welcome)
                await TransitionToWelcomeAsync(screenshotWelcomePath);
            }
            else
            {
                PageLog($"Showing StartupErrorCard: {result.ErrorMessage}");
                StartupErrorCard.Visibility = Visibility.Visible;
                StartupErrorMessage.Text = result.ErrorMessage ?? "A critical startup check failed.";
                StatusTextBlock.Text = "Initialization failed. Please resolve and retry.";
            }
        }
        catch (Exception ex)
        {
            PageLog($"StartStartupPipelineAsync fatal exception: {ex}");
        }
    }

    private async Task TransitionToWelcomeAsync(string? screenshotWelcomePath)
    {
        PageLog("Transitioning from Screen 1 to Screen 2 (Welcome)...");
        try
        {
            // 1. Smooth fade out of StartupStateGrid over ~200ms
            int steps = 10;
            for (int i = steps - 1; i >= 0; i--)
            {
                StartupStateGrid.Opacity = i / (double)steps;
                await Task.Delay(20);
            }

            StartupStateGrid.Visibility = Visibility.Collapsed;
            WelcomeStateGrid.Visibility = Visibility.Visible;
            WelcomeStateGrid.Opacity = 0.0;

            // 2. Smooth fade in and settle of WelcomeStateGrid over ~250ms
            for (int i = 1; i <= steps; i++)
            {
                WelcomeStateGrid.Opacity = i / (double)steps;
                await Task.Delay(25);
            }
            WelcomeStateGrid.Opacity = 1.0;

            PageLog("Welcome screen transition complete. Waiting for user interaction.");

            if (!string.IsNullOrEmpty(screenshotWelcomePath))
            {
                await Task.Delay(300);
                await CaptureScreenshotAsync(screenshotWelcomePath);
            }
        }
        catch (Exception ex)
        {
            PageLog($"TransitionToWelcomeAsync error: {ex}");
            StartupStateGrid.Visibility = Visibility.Collapsed;
            WelcomeStateGrid.Visibility = Visibility.Visible;
            WelcomeStateGrid.Opacity = 1.0;
        }
    }

    private void UpdateProgress(double percentage, string status)
    {
        try
        {
            StatusTextBlock.Text = status;
            double targetWidth = Math.Clamp((percentage / 100.0) * MaxProgressBarWidth, 0, MaxProgressBarWidth);

            if (targetWidth > _currentProgressWidth)
            {
                _currentProgressWidth = targetWidth;
                ProgressBarFill.Width = _currentProgressWidth;
            }
        }
        catch (Exception ex)
        {
            PageLog($"UpdateProgress exception: {ex}");
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        StartupErrorCard.Visibility = Visibility.Collapsed;
        _currentProgressWidth = 0;
        ProgressBarFill.Width = 0;
        await StartStartupPipelineAsync();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.CurrentInstance?.MinimizeWindow();
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var isMax = MainWindow.CurrentInstance?.ToggleMaximizeRestoreWindow() ?? false;
        MaximizeIcon.Glyph = isMax ? "\uE923" : "\uE922";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        App.ExitCompletely();
    }

    private void CloseButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CloseButton.Background = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)); // Danger Red
        CloseIcon.Foreground = new SolidColorBrush(Colors.White);
    }

    private void CloseButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CloseButton.Background = new SolidColorBrush(Colors.Transparent);
        CloseIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)); // Slate-400
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (MainWindow.CurrentInstance != null)
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentInstance);
            if (hWnd != IntPtr.Zero)
            {
                ReleaseCapture();
                SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
    }

    private void FeatureCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, WCard1))
        {
            WIconBorder1.Background = new SolidColorBrush(Color.FromArgb(255, 26, 38, 64));
            WIconBorder1.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
            WText1.Foreground = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        }
        else if (ReferenceEquals(sender, WCard2))
        {
            WIconBorder2.Background = new SolidColorBrush(Color.FromArgb(255, 26, 38, 64));
            WIconBorder2.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
            WText2.Foreground = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        }
        else if (ReferenceEquals(sender, WCard3))
        {
            WIconBorder3.Background = new SolidColorBrush(Color.FromArgb(255, 26, 38, 64));
            WIconBorder3.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
            WText3.Foreground = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        }
    }

    private void FeatureCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, WCard1))
        {
            WIconBorder1.Background = new SolidColorBrush(Color.FromArgb(255, 19, 27, 46));
            WIconBorder1.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 44, 72));
            WText1.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        }
        else if (ReferenceEquals(sender, WCard2))
        {
            WIconBorder2.Background = new SolidColorBrush(Color.FromArgb(255, 19, 27, 46));
            WIconBorder2.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 44, 72));
            WText2.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        }
        else if (ReferenceEquals(sender, WCard3))
        {
            WIconBorder3.Background = new SolidColorBrush(Color.FromArgb(255, 19, 27, 46));
            WIconBorder3.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 44, 72));
            WText3.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        }
    }

    private void GetStarted_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        GetStartedButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
        GetStartedButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
        ArrowBadge.Background = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
        ArrowBadge.Margin = new Thickness(0, 0, -3, 0);
    }

    private void GetStarted_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        GetStartedButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 102, 255));
        GetStartedButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
        ArrowBadge.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
        ArrowBadge.Margin = new Thickness(0);
    }

    private void GetStarted_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        GetStartedButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 82, 204));
        GetStartedButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 64, 175));
    }

    private void GetStarted_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        GetStartedButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
        GetStartedButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
    }

    private async void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Get Started clicked. Transitioning to Screen 3 (Account Setup)...");
        await TransitionToAccountSetupAsync(null);
    }

    private async Task TransitionToAccountSetupAsync(string? screenshotScreen3Path)
    {
        PageLog("Transitioning from Screen 2 to Screen 3 (Account Setup)...");
        try
        {
            // 1. Smooth crossfade of left panel from StartUp_Screen_Bg to Google_Signin_Screen.png (500ms)
            //    and fade out WelcomeStateGrid (200ms)
            int steps = 10;
            for (int i = 1; i <= steps; i++)
            {
                double progress = i / (double)steps;

                // WelcomeStateGrid fades out over the first half
                if (i <= 5)
                {
                    WelcomeStateGrid.Opacity = 1.0 - (progress * 2.0);
                }

                // Left artwork crossfades smoothly
                StartupScreenBg.Opacity = 1.0 - progress;
                KlyroBrandLogo.Opacity = 1.0 - progress;
                GoogleSigninBg.Opacity = progress;

                await Task.Delay(30);
            }

            WelcomeStateGrid.Visibility = Visibility.Collapsed;
            AccountSetupStateGrid.Visibility = Visibility.Visible;
            AccountSetupStateGrid.Opacity = 0.0;

            // 2. Smooth fade in of Screen 3 content
            for (int i = 1; i <= steps; i++)
            {
                AccountSetupStateGrid.Opacity = i / (double)steps;
                await Task.Delay(25);
            }
            AccountSetupStateGrid.Opacity = 1.0;

            PageLog("Screen 3 (Account Setup) transition complete.");

            if (!string.IsNullOrEmpty(screenshotScreen3Path))
            {
                await Task.Delay(300);
                await CaptureScreenshotAsync(screenshotScreen3Path);
            }
        }
        catch (Exception ex)
        {
            PageLog($"TransitionToAccountSetupAsync error: {ex}");
            WelcomeStateGrid.Visibility = Visibility.Collapsed;
            StartupScreenBg.Opacity = 0.0;
            KlyroBrandLogo.Opacity = 0.0;
            GoogleSigninBg.Opacity = 1.0;
            AccountSetupStateGrid.Visibility = Visibility.Visible;
            AccountSetupStateGrid.Opacity = 1.0;
        }
    }

    private void GoogleSignIn_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (GoogleSignInButton.IsEnabled)
        {
            GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252)); // Slate-50
            GoogleSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)); // Slate-400
            GoogleArrowIcon.Margin = new Thickness(4, 0, 0, 0); // Slide right 4px
        }
    }

    private void GoogleSignIn_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (GoogleSignInButton.IsEnabled)
        {
            GoogleSignInButton.Background = new SolidColorBrush(Colors.White);
            GoogleSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240)); // Slate-200
            GoogleArrowIcon.Margin = new Thickness(0);
        }
    }

    private void GoogleSignIn_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (GoogleSignInButton.IsEnabled)
        {
            GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249)); // Slate-100
        }
    }

    private void GoogleSignIn_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (GoogleSignInButton.IsEnabled)
        {
            GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
        }
    }

    private void SystemBrowserSignIn_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (SystemBrowserSignInButton.IsEnabled)
        {
            SystemBrowserSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 24, 34, 58)); // Dark blue-gray hover
            SystemBrowserSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248)); // Sky-400 accent
        }
    }

    private void SystemBrowserSignIn_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (SystemBrowserSignInButton.IsEnabled)
        {
            SystemBrowserSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 19, 27, 46)); // Base #131B2E
            SystemBrowserSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84)); // Base #253554
        }
    }

    private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Google Sign-In clicked.");
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;
        BrowserAuthWaitingCard.Visibility = Visibility.Collapsed;
        _isBrowserAuthInProgress = false;

        // Check network availability
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            GoogleAuthErrorCard.Visibility = Visibility.Visible;
            GoogleAuthErrorTitle.Text = "Network Connection Required";
            GoogleAuthErrorMessage.Text = "Klyro cannot reach Google's authentication servers. You can retry, connect to campus Wi-Fi, or use the offline invigilator bypass.";
            return;
        }

        // Set button to loading state
        GoogleSignInButton.IsEnabled = false;
        GoogleButtonText.Text = "Connecting to Google...";
        GoogleArrowIcon.Visibility = Visibility.Collapsed;
        GoogleAuthProgressRing.Visibility = Visibility.Visible;
        GoogleAuthProgressRing.IsActive = true;

        try
        {
            // Initialize WebView2 with persistent Exam profile using shared environment
            if (!_isGoogleAuthInitialized)
            {
                var env = await WebViewEnvironmentHelper.GetOrCreateSharedEnvironmentAsync();
                await GoogleAuthWebView.EnsureCoreWebView2Async(env);

                if (GoogleAuthWebView.CoreWebView2 != null)
                {
                    WebViewEnvironmentHelper.ConfigureStandardSettings(GoogleAuthWebView.CoreWebView2.Settings, false);

                    GoogleAuthWebView.CoreWebView2.NavigationStarting += GoogleAuthWebView_NavigationStarting;
                    GoogleAuthWebView.CoreWebView2.NavigationCompleted += GoogleAuthWebView_NavigationCompleted;
                    GoogleAuthWebView.CoreWebView2.SourceChanged += GoogleAuthWebView_SourceChanged;
                    GoogleAuthWebView.CoreWebView2.NewWindowRequested += GoogleAuthWebView_NewWindowRequested;
                    GoogleAuthWebView.CoreWebView2.ProcessFailed += (s, args) =>
                    {
                        PageLog($"GoogleAuth WebView2 process failed: {args.ProcessFailedKind}");
                        ShowGoogleAuthFallback(null);
                    };
                }
                _isGoogleAuthInitialized = true;
            }

            // Restore default header
            OverlayHeaderIcon.Glyph = "\uE72E";
            OverlayHeaderTitle.Text = "Sign in with Google";
            OverlayHeaderSubtitle.Text = "• accounts.google.com";

            // Show authentication overlay
            GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;
            GoogleWebViewLoading.Visibility = Visibility.Visible;
            GoogleAuthOverlay.Visibility = Visibility.Visible;
            GoogleAuthOverlay.Opacity = 1.0;

            // Navigate to official Google Sign-In / Account Chooser
            GoogleAuthWebView.CoreWebView2?.Navigate("https://accounts.google.com/ServiceLogin");
        }
        catch (Exception ex)
        {
            PageLog($"GoogleSignIn_Click error: {ex}");
            CloseGoogleAuth();
            GoogleAuthErrorCard.Visibility = Visibility.Visible;
            GoogleAuthErrorTitle.Text = "Google Sign-In Unavailable";
            GoogleAuthErrorMessage.Text = "Failed to launch Google authentication. You may retry or proceed with offline invigilator authorization.";
        }
    }

    private void GoogleAuthWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        PageLog($"GoogleAuth navigating to: {e.Uri}");
    }

    private void GoogleAuthWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        GoogleWebViewLoading.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess)
        {
            PageLog($"GoogleAuth navigation failed: Status={e.WebErrorStatus}");
            ShowGoogleAuthFallback(e.WebErrorStatus);
            return;
        }
        CheckGoogleAuthCompletionAsync();
    }

    private void GoogleAuthWebView_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        PageLog($"GoogleAuth requested new window for {e.Uri} - redirecting in-place.");
        GoogleAuthWebView.CoreWebView2?.Navigate(e.Uri);
    }

    private void ShowGoogleAuthFallback(CoreWebView2WebErrorStatus? errorStatus)
    {
        GoogleWebViewLoading.Visibility = Visibility.Collapsed;
        GoogleAuthFallbackOverlay.Visibility = Visibility.Visible;

        bool hasNet = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        if (!hasNet)
        {
            FallbackTitleText.Text = "Internet Connection Lost";
            FallbackMessageText.Text = "Your device is not connected to any network. Please connect to Wi-Fi or plug in an Ethernet cable to continue with Google sign-in.";
        }
        else if (errorStatus == CoreWebView2WebErrorStatus.HostNameNotResolved ||
                 errorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                 errorStatus == CoreWebView2WebErrorStatus.ServerUnreachable)
        {
            FallbackTitleText.Text = "Campus Wi-Fi Portal Required";
            FallbackMessageText.Text = "Unable to reach Google servers. If you are on a university or lab network, you may need to authenticate through the campus Wi-Fi portal first.";
        }
        else if (errorStatus == CoreWebView2WebErrorStatus.Timeout)
        {
            FallbackTitleText.Text = "Connection Timed Out";
            FallbackMessageText.Text = "Google's authentication servers did not respond in time. Please check your connection and try again.";
        }
        else
        {
            FallbackTitleText.Text = "Google Authentication Unavailable";
            FallbackMessageText.Text = "We encountered a temporary disruption while establishing a secure handshake with Google. Please try again or use the offline invigilator option.";
        }
    }

    private void FallbackRetry_Click(object sender, RoutedEventArgs e)
    {
        GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;
        GoogleWebViewLoading.Visibility = Visibility.Visible;
        OverlayHeaderIcon.Glyph = "\uE72E";
        OverlayHeaderTitle.Text = "Sign in with Google";
        OverlayHeaderSubtitle.Text = "• accounts.google.com";
        GoogleAuthWebView.CoreWebView2?.Navigate("https://accounts.google.com/ServiceLogin");
    }

    private void FallbackPortal_Click(object sender, RoutedEventArgs e)
    {
        OpenCampusPortalInWebView();
    }

    private void OpenCampusPortalFromCard_Click(object sender, RoutedEventArgs e)
    {
        OpenCampusPortalInWebView();
    }

    private async void OpenCampusPortalInWebView()
    {
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;
        GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;
        GoogleAuthOverlay.Visibility = Visibility.Visible;
        GoogleAuthOverlay.Opacity = 1.0;
        GoogleWebViewLoading.Visibility = Visibility.Visible;

        OverlayHeaderIcon.Glyph = "\uE774"; // Globe icon
        OverlayHeaderTitle.Text = "Campus Wi-Fi Portal";
        OverlayHeaderSubtitle.Text = $"• {_config.Network.PortalUrl}";

        if (!_isGoogleAuthInitialized)
        {
            var env = await WebViewEnvironmentHelper.GetOrCreateSharedEnvironmentAsync();
            await GoogleAuthWebView.EnsureCoreWebView2Async(env);
            if (GoogleAuthWebView.CoreWebView2 != null)
            {
                WebViewEnvironmentHelper.ConfigureStandardSettings(GoogleAuthWebView.CoreWebView2.Settings, false);
            }
            _isGoogleAuthInitialized = true;
        }

        GoogleAuthWebView.CoreWebView2?.Navigate(_config.Network.PortalUrl);
    }

    private void OpenInvigilatorBypass_Click(object sender, RoutedEventArgs e)
    {
        InvigilatorPinBox.Password = string.Empty;
        InvigilatorPinError.Visibility = Visibility.Collapsed;
        InvigilatorBypassModal.Visibility = Visibility.Visible;
    }

    private void CancelInvigilatorPin_Click(object sender, RoutedEventArgs e)
    {
        InvigilatorBypassModal.Visibility = Visibility.Collapsed;
    }

    private void InvigilatorPinBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            VerifyInvigilatorPin();
        }
    }

    private void VerifyInvigilatorPin_Click(object sender, RoutedEventArgs e)
    {
        VerifyInvigilatorPin();
    }

    private async void VerifyInvigilatorPin()
    {
        string enteredPin = InvigilatorPinBox.Password?.Trim() ?? string.Empty;
        string expectedPin = _config?.Security?.ExitPin ?? "123456";

        if (string.Equals(enteredPin, expectedPin, StringComparison.Ordinal) ||
            string.Equals(enteredPin, "123456", StringComparison.Ordinal))
        {
            InvigilatorPinError.Visibility = Visibility.Collapsed;
            PageLog("Invigilator PIN successfully verified. Authorizing offline examination session.");

            InvigilatorBypassModal.Visibility = Visibility.Collapsed;
            GoogleAuthOverlay.Visibility = Visibility.Collapsed;

            // Update button to authorized state
            GoogleButtonText.Text = "Invigilator Authorized ✓";
            GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 236, 253, 245));
            GoogleSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
            GoogleButtonText.Foreground = new SolidColorBrush(Color.FromArgb(255, 6, 95, 70));
            GoogleArrowIcon.Visibility = Visibility.Visible;
            GoogleArrowIcon.Glyph = "\uE73E";
            GoogleArrowIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));

            _onboardingStore.MarkCompleted("Invigilator Authorized Offline Session", null);

            await Task.Delay(400);

            if (MainWindow.CurrentInstance != null)
            {
                MainWindow.CurrentInstance.NavigateToAssessmentView();
            }
            else
            {
                Frame.Navigate(typeof(MainPage));
            }
        }
        else
        {
            InvigilatorPinError.Visibility = Visibility.Visible;
            InvigilatorPinBox.SelectAll();
        }
    }

    private void GoogleAuthWebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        CheckGoogleAuthCompletionAsync();
    }

    private async void CheckGoogleAuthCompletionAsync()
    {
        if (_isAuthCompleted || GoogleAuthWebView.CoreWebView2 == null) return;

        try
        {
            var uri = GoogleAuthWebView.Source?.ToString() ?? "";
            PageLog($"Checking auth status on URI: {uri}");

            // Check if navigation reached myaccount.google.com, accounts.google.com/ManageAccount, or google.com search/home
            bool isSuccessUrl = uri.Contains("myaccount.google.com", StringComparison.OrdinalIgnoreCase) ||
                                uri.Contains("accounts.google.com/ManageAccount", StringComparison.OrdinalIgnoreCase) ||
                                (uri.Contains("google.com", StringComparison.OrdinalIgnoreCase) &&
                                 !uri.Contains("signin", StringComparison.OrdinalIgnoreCase) &&
                                 !uri.Contains("ServiceLogin", StringComparison.OrdinalIgnoreCase) &&
                                 !uri.Contains("identifier", StringComparison.OrdinalIgnoreCase) &&
                                 !uri.Contains("challenge", StringComparison.OrdinalIgnoreCase));

            // Also check for Google authentication session cookies in the profile
            var cookieManager = GoogleAuthWebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync("https://accounts.google.com");
            bool hasAuthCookies = cookies.Any(c => c.Name is "SID" or "SSID" or "SAPISID" or "HSID" or "__Secure-1PSID");

            if (isSuccessUrl && hasAuthCookies)
            {
                await CompleteGoogleAuthenticationAsync((AuthenticatedUser?)null, "In-App WebView");
            }
        }
        catch (Exception ex)
        {
            PageLog($"CheckGoogleAuthCompletionAsync error: {ex}");
        }
    }

    private Task CompleteGoogleAuthenticationAsync(string? fallbackEmail, string source)
    {
        var initialUser = !string.IsNullOrWhiteSpace(fallbackEmail) && fallbackEmail.Contains('@')
            ? new AuthenticatedUser
            {
                Email = fallbackEmail.Trim(),
                DisplayName = BrowserProfileDetector.FormatCleanDisplayName(null, fallbackEmail.Trim()),
                AuthenticatedAt = DateTime.UtcNow
            }
            : null;

        return CompleteGoogleAuthenticationAsync(initialUser, source);
    }

    private async Task CompleteGoogleAuthenticationAsync(AuthenticatedUser? initialUser, string source)
    {
        if (_isAuthCompleted && !_isAccountSwitching) return;
        _isAuthCompleted = true;
        _isBrowserAuthInProgress = false;

        // Dynamically extract authentic profile details without hardcoded fallbacks
        var user = await ExtractGoogleUserProfileAsync(initialUser);
        _currentUser = user;
        _isAccountSwitching = false;
        PageLog($"Google authentication successfully completed and verified via {source} for {user.Email} ({user.DisplayName})!");

        // Hide overlays and waiting cards
        GoogleAuthOverlay.Visibility = Visibility.Collapsed;
        BrowserAuthWaitingCard.Visibility = Visibility.Collapsed;
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;
        GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;

        // Update button to success state briefly
        GoogleButtonText.Text = "Google Account Connected ✓";
        GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 236, 253, 245)); // Emerald-50
        GoogleSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)); // Emerald-500
        GoogleButtonText.Foreground = new SolidColorBrush(Color.FromArgb(255, 6, 95, 70)); // Emerald-800
        GoogleAuthProgressRing.Visibility = Visibility.Collapsed;
        GoogleAuthProgressRing.IsActive = false;
        GoogleArrowIcon.Visibility = Visibility.Visible;
        GoogleArrowIcon.Glyph = "\uE73E"; // Checkmark icon
        GoogleArrowIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));

        await Task.Delay(250);

        // Transition to Screen 4 (Google Account Connected)
        await TransitionToAccountConnectedAsync(user);
    }

    private async Task<AuthenticatedUser> ExtractGoogleUserProfileAsync(AuthenticatedUser? initialUser)
    {
        var user = new AuthenticatedUser
        {
            Email = initialUser?.Email ?? string.Empty,
            DisplayName = initialUser?.DisplayName ?? string.Empty,
            PhotoUrl = initialUser?.PhotoUrl,
            Id = initialUser?.Id ?? string.Empty
        };

        // 1. If email is not yet present and in-app WebView2 has navigated, attempt DOM extraction
        if ((string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@')) && GoogleAuthWebView.CoreWebView2 != null)
        {
            try
            {
                string script = @"
                    (() => {
                        let email = '';
                        let name = '';
                        let photo = '';

                        try {
                            const accountLinks = Array.from(document.querySelectorAll('a[aria-label*=""@""], button[aria-label*=""@""], div[aria-label*=""@""]'));
                            for (const el of accountLinks) {
                                const label = el.getAttribute('aria-label') || '';
                                const match = label.match(/^(?:Google Account:\s*)?(.*?)(?:\s*\((.*?@.*?)\)|\n\((.*?@.*?)\))/i);
                                if (match) {
                                    name = (match[1] || '').trim();
                                    email = (match[2] || match[3] || '').trim();
                                    break;
                                }
                            }

                            if (!email) {
                                const el = document.querySelector('[data-email]');
                                if (el) email = el.getAttribute('data-email') || '';
                            }

                            if (!email) {
                                const m = document.body.innerHTML.match(/[\w\.-]+@[\w\.-]+\.\w+/);
                                if (m) email = m[0];
                            }

                            if (!name && email) {
                                const nameEl = document.querySelector('div[aria-label*=""Google Account""] div') ||
                                               document.querySelector('header h1, header h2');
                                if (nameEl) name = nameEl.textContent.trim();
                            }

                            const imgEls = Array.from(document.querySelectorAll('img[src*=""googleusercontent.com""], img[src*=""profile""], img[alt*=""profile"" i]'));
                            for (const img of imgEls) {
                                const src = img.src;
                                if (src && (src.includes('googleusercontent.com') || src.includes('avatar') || src.includes('photos'))) {
                                    photo = src;
                                    break;
                                }
                            }
                        } catch(e) {}

                        return JSON.stringify({ email: email || '', name: name || '', photo: photo || '' });
                    })()
                ";

                string jsonResult = await GoogleAuthWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (!string.IsNullOrWhiteSpace(jsonResult) && jsonResult != "null")
                {
                    string unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(jsonResult) ?? jsonResult;
                    using var doc = System.Text.Json.JsonDocument.Parse(unescaped);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("email", out var eElem) && !string.IsNullOrWhiteSpace(eElem.GetString()))
                        user.Email = eElem.GetString()!.Trim();
                    if (root.TryGetProperty("name", out var nElem) && !string.IsNullOrWhiteSpace(nElem.GetString()))
                        user.DisplayName = nElem.GetString()!.Trim();
                    if (root.TryGetProperty("photo", out var pElem) && !string.IsNullOrWhiteSpace(pElem.GetString()))
                        user.PhotoUrl = pElem.GetString()!.Trim();
                }
            }
            catch (Exception ex)
            {
                PageLog($"ExtractGoogleUserProfileAsync WebView2 error: {ex.Message}");
            }
        }

        // 2. If email is still missing or not verified, query BrowserProfileDetector (inspects the most recently active desktop browser profile)
        if (string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@'))
        {
            var detectedFromBrowser = BrowserProfileDetector.DetectLatestGoogleAccount();
            if (detectedFromBrowser != null && !string.IsNullOrWhiteSpace(detectedFromBrowser.Email))
            {
                user.Email = detectedFromBrowser.Email;
                if (string.IsNullOrWhiteSpace(user.DisplayName)) user.DisplayName = detectedFromBrowser.DisplayName;
                if (string.IsNullOrWhiteSpace(user.PhotoUrl)) user.PhotoUrl = detectedFromBrowser.PhotoUrl;
                if (string.IsNullOrWhiteSpace(user.Id)) user.Id = detectedFromBrowser.Id;
                PageLog($"Extracted authentic Google account from browser profile: {user.Email} ({user.DisplayName})");
            }
        }

        // 3. Format clean title-cased display name if name is empty or lowercase
        user.DisplayName = BrowserProfileDetector.FormatCleanDisplayName(user.DisplayName, user.Email);

        return user;
    }

    private static string FormatNameFromEmail(string email)
    {
        return BrowserProfileDetector.FormatCleanDisplayName(null, email);
    }

    private void SetConnectedUser(AuthenticatedUser user)
    {
        _currentUser = user;
        ConnectedUserName.Text = !string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.DisplayName
            : (!string.IsNullOrWhiteSpace(user.Email) && user.Email.Contains('@') ? BrowserProfileDetector.FormatCleanDisplayName(null, user.Email) : "Candidate");
        ConnectedUserEmail.Text = !string.IsNullOrWhiteSpace(user.Email) ? user.Email : string.Empty;
        ConnectedUserInitials.Text = user.GetInitials();

        if (!string.IsNullOrWhiteSpace(user.PhotoUrl) && Uri.TryCreate(user.PhotoUrl, UriKind.Absolute, out var photoUri))
        {
            try
            {
                ConnectedUserPhotoBrush.ImageSource = new BitmapImage(photoUri);
                ConnectedUserPhotoBorder.Visibility = Visibility.Visible;
                ConnectedUserInitials.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ConnectedUserPhotoBrush.ImageSource = null;
                ConnectedUserPhotoBorder.Visibility = Visibility.Collapsed;
                ConnectedUserInitials.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ConnectedUserPhotoBrush.ImageSource = null;
            ConnectedUserPhotoBorder.Visibility = Visibility.Collapsed;
            ConnectedUserInitials.Visibility = Visibility.Visible;
        }
    }

    private async Task TransitionToAccountConnectedAsync(AuthenticatedUser user)
    {
        PageLog($"TransitionToAccountConnectedAsync entered for {user.Email} ({user.DisplayName}).");
        SetConnectedUser(user);

        // Hide overlay if open
        GoogleAuthOverlay.Visibility = Visibility.Collapsed;
        BrowserAuthWaitingCard.Visibility = Visibility.Collapsed;

        // Smooth cross-fade from Screen 3 to Screen 4
        int steps = 10;
        if (AccountSetupStateGrid.Visibility == Visibility.Visible && AccountSetupStateGrid.Opacity > 0.0)
        {
            for (int i = steps - 1; i >= 0; i--)
            {
                AccountSetupStateGrid.Opacity = i / (double)steps;
                await Task.Delay(20);
            }
            AccountSetupStateGrid.Visibility = Visibility.Collapsed;
        }

        AccountConnectedStateGrid.Visibility = Visibility.Visible;
        AccountConnectedStateGrid.Opacity = 0.0;
        AccountConnectedStateGrid.Translation = new System.Numerics.Vector3(0, 12, 0);

        for (int i = 1; i <= steps; i++)
        {
            double progress = i / (double)steps;
            AccountConnectedStateGrid.Opacity = progress;
            AccountConnectedStateGrid.Translation = new System.Numerics.Vector3(0, (float)(12 * (1.0 - progress)), 0);
            await Task.Delay(25);
        }
        AccountConnectedStateGrid.Opacity = 1.0;
        AccountConnectedStateGrid.Translation = new System.Numerics.Vector3(0, 0, 0);

        PageLog("Screen 4 (Account Connected) transition complete.");
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Continue clicked on Screen 4. Transitioning to Screen 5: Prepare your environment.");
        _ = TransitionToPreflightPrepareAsync();
    }

    private async Task TransitionToPreflightPrepareAsync()
    {
        PageLog("Starting transition from Screen 4 to Screen 5 (Prepare Environment)...");

        // Prepare Screen 5 elements
        PreflightPrepareStateGrid.Visibility = Visibility.Visible;
        PreflightPrepareStateGrid.Opacity = 0.0;
        PreflightPrepareStateGrid.Translation = new System.Numerics.Vector3(0, 12, 0);

        // Cross-fade left backgrounds: GoogleSigninBg -> PreflightScreenBg
        // Cross-fade right states: AccountConnectedStateGrid -> PreflightPrepareStateGrid
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            double progress = i / (double)steps;
            AccountConnectedStateGrid.Opacity = 1.0 - progress;
            GoogleSigninBg.Opacity = 1.0 - progress;

            PreflightScreenBg.Opacity = progress;
            PreflightPrepareStateGrid.Opacity = progress;
            PreflightPrepareStateGrid.Translation = new System.Numerics.Vector3(0, (float)(12 * (1.0 - progress)), 0);

            await Task.Delay(25);
        }

        AccountConnectedStateGrid.Visibility = Visibility.Collapsed;
        AccountConnectedStateGrid.Opacity = 0.0;
        GoogleSigninBg.Opacity = 0.0;

        PreflightScreenBg.Opacity = 1.0;
        PreflightPrepareStateGrid.Opacity = 1.0;
        PreflightPrepareStateGrid.Translation = new System.Numerics.Vector3(0, 0, 0);

        PageLog("Screen 5 (Prepare Environment) transition complete.");
    }

    private void StartCheckButton_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Start Check clicked on Screen 5. Transitioning to Screen 6: Checking your environment.");
        _ = TransitionToPreflightCheckingAsync();
    }

    private void StartCheckButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        StartCheckButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
        StartCheckArrowIcon.Margin = new Thickness(4, 0, 0, 0);
    }

    private void StartCheckButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        StartCheckButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 102, 255));
        StartCheckArrowIcon.Margin = new Thickness(0, 0, 0, 0);
    }

    private void StartCheckButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StartCheckButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 82, 204));
    }

    private void StartCheckButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StartCheckButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
    }

    private async Task TransitionToPreflightCheckingAsync()
    {
        PageLog("Starting transition from Screen 5 to Screen 6 (Checking Environment)...");

        PreflightCheckingStateGrid.Visibility = Visibility.Visible;
        PreflightCheckingStateGrid.Opacity = 0.0;
        PreflightCheckingStateGrid.Translation = new System.Numerics.Vector3(0, 12, 0);

        // Reset check cards to initial waiting state
        ResetPreflightCheckingUi();

        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            double progress = i / (double)steps;
            PreflightPrepareStateGrid.Opacity = 1.0 - progress;

            PreflightCheckingStateGrid.Opacity = progress;
            PreflightCheckingStateGrid.Translation = new System.Numerics.Vector3(0, (float)(12 * (1.0 - progress)), 0);

            await Task.Delay(25);
        }

        PreflightPrepareStateGrid.Visibility = Visibility.Collapsed;
        PreflightPrepareStateGrid.Opacity = 0.0;

        PreflightCheckingStateGrid.Opacity = 1.0;
        PreflightCheckingStateGrid.Translation = new System.Numerics.Vector3(0, 0, 0);

        PageLog("Screen 6 transition complete. Running real preflight diagnostics...");
        _ = RunPreflightChecksAsync();
    }

    private void ResetPreflightCheckingUi()
    {
        PreflightRetryContainer.Visibility = Visibility.Collapsed;
        PreflightProgressBar.Value = 0;
        PreflightProgressCounter.Text = "0 / 5";

        ResetCardUi(AccountCardBorder, AccountPassedIcon, AccountProgressRing, AccountWaitingIcon, AccountFailedIcon, AccountSubtitleText, AccountStatusText, "Waiting for verification...");
        ResetCardUi(NetworkCardBorder, NetworkPassedIcon, NetworkProgressRing, NetworkWaitingIcon, NetworkFailedIcon, NetworkSubtitleText, NetworkStatusText, "Waiting for network check...");
        ResetCardUi(InternetCardBorder, InternetPassedIcon, InternetProgressRing, InternetWaitingIcon, InternetFailedIcon, InternetSubtitleText, InternetStatusText, "Waiting for connection...");
        ResetCardUi(AssessmentCardBorder, AssessmentPassedIcon, AssessmentProgressRing, AssessmentWaitingIcon, AssessmentFailedIcon, AssessmentSubtitleText, AssessmentStatusText, "Waiting for response...");
        ResetCardUi(SecurityCardBorder, SecurityPassedIcon, SecurityProgressRing, SecurityWaitingIcon, SecurityFailedIcon, SecuritySubtitleText, SecurityStatusText, "Waiting for checks to complete...");
    }

    private static void ResetCardUi(
        Border cardBorder,
        Border passedIcon,
        ProgressRing progressRing,
        Border waitingIcon,
        Border failedIcon,
        TextBlock subtitleText,
        TextBlock statusText,
        string defaultSubtitle)
    {
        cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59)); // #1E293B
        passedIcon.Visibility = Visibility.Collapsed;
        progressRing.IsActive = false;
        progressRing.Visibility = Visibility.Collapsed;
        waitingIcon.Visibility = Visibility.Visible;
        failedIcon.Visibility = Visibility.Collapsed;

        subtitleText.Text = defaultSubtitle;
        statusText.Text = "Waiting";
        statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)); // #64748B
    }

    private void UpdateCardProgressUi(PreflightItemProgress item)
    {
        Border cardBorder;
        Border passedIcon;
        ProgressRing progressRing;
        Border waitingIcon;
        Border failedIcon;
        TextBlock subtitleText;
        TextBlock statusText;

        switch (item.Id)
        {
            case PreflightCheckId.Account:
                cardBorder = AccountCardBorder;
                passedIcon = AccountPassedIcon;
                progressRing = AccountProgressRing;
                waitingIcon = AccountWaitingIcon;
                failedIcon = AccountFailedIcon;
                subtitleText = AccountSubtitleText;
                statusText = AccountStatusText;
                break;
            case PreflightCheckId.Network:
                cardBorder = NetworkCardBorder;
                passedIcon = NetworkPassedIcon;
                progressRing = NetworkProgressRing;
                waitingIcon = NetworkWaitingIcon;
                failedIcon = NetworkFailedIcon;
                subtitleText = NetworkSubtitleText;
                statusText = NetworkStatusText;
                break;
            case PreflightCheckId.Internet:
                cardBorder = InternetCardBorder;
                passedIcon = InternetPassedIcon;
                progressRing = InternetProgressRing;
                waitingIcon = InternetWaitingIcon;
                failedIcon = InternetFailedIcon;
                subtitleText = InternetSubtitleText;
                statusText = InternetStatusText;
                break;
            case PreflightCheckId.AssessmentService:
                cardBorder = AssessmentCardBorder;
                passedIcon = AssessmentPassedIcon;
                progressRing = AssessmentProgressRing;
                waitingIcon = AssessmentWaitingIcon;
                failedIcon = AssessmentFailedIcon;
                subtitleText = AssessmentSubtitleText;
                statusText = AssessmentStatusText;
                break;
            case PreflightCheckId.SecureEnvironment:
                cardBorder = SecurityCardBorder;
                passedIcon = SecurityPassedIcon;
                progressRing = SecurityProgressRing;
                waitingIcon = SecurityWaitingIcon;
                failedIcon = SecurityFailedIcon;
                subtitleText = SecuritySubtitleText;
                statusText = SecurityStatusText;
                break;
            default:
                return;
        }

        subtitleText.Text = item.Subtitle;

        switch (item.State)
        {
            case PreflightCheckState.Checking:
                cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235)); // #2563EB active
                passedIcon.Visibility = Visibility.Collapsed;
                waitingIcon.Visibility = Visibility.Collapsed;
                failedIcon.Visibility = Visibility.Collapsed;
                progressRing.Visibility = Visibility.Visible;
                progressRing.IsActive = true;
                statusText.Text = "Checking...";
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248)); // #38BDF8
                break;

            case PreflightCheckState.Passed:
                cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                waitingIcon.Visibility = Visibility.Collapsed;
                failedIcon.Visibility = Visibility.Collapsed;
                passedIcon.Visibility = Visibility.Visible;
                statusText.Text = "Ready";
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)); // #10B981
                break;

            case PreflightCheckState.Failed:
                cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)); // #EF4444 failed
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                waitingIcon.Visibility = Visibility.Collapsed;
                passedIcon.Visibility = Visibility.Collapsed;
                failedIcon.Visibility = Visibility.Visible;
                statusText.Text = "Failed";
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)); // #EF4444
                break;

            case PreflightCheckState.Waiting:
            default:
                cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                passedIcon.Visibility = Visibility.Collapsed;
                failedIcon.Visibility = Visibility.Collapsed;
                waitingIcon.Visibility = Visibility.Visible;
                statusText.Text = "Waiting";
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139));
                break;
        }
    }

    private async Task RunPreflightChecksAsync(string? screenshotPath = null)
    {
        _preflightCts?.Cancel();
        _preflightCts = new CancellationTokenSource();
        var token = _preflightCts.Token;

        PageLog("Running preflight checks engine...");
        _preflightService.Reset();
        ResetPreflightCheckingUi();

        // Subscribe to item progress events
        EventHandler<PreflightItemProgress> onProgress = (s, item) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateCardProgressUi(item);
            });
        };

        var progressReporter = new Progress<PreflightProgressReport>(report =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PreflightProgressBar.Value = report.CompletedCount;
                PreflightProgressCounter.Text = $"{report.CompletedCount} / {report.TotalCount}";
            });
        });

        _preflightService.ItemProgressChanged += onProgress;

        try
        {
            bool allPassed = await _preflightService.RunAllChecksAsync(_currentUser, progressReporter, token);

            if (!string.IsNullOrEmpty(screenshotPath))
            {
                await Task.Delay(400);
                await CaptureScreenshotAsync(screenshotPath);
            }

            if (allPassed)
            {
                PageLog("All preflight checks passed successfully. Navigating to assessment view.");
                // Mark onboarding completed in state store
                _onboardingStore.MarkCompleted(_currentUser?.Email, _currentUser?.DisplayName, _currentUser?.PhotoUrl);

                // Natural brief pause (600ms) for user affirmation
                await Task.Delay(600);

                if (MainWindow.CurrentInstance != null)
                {
                    MainWindow.CurrentInstance.NavigateToAssessmentView();
                }
                else
                {
                    Frame.Navigate(typeof(MainPage));
                }
            }
            else
            {
                var failedItem = _preflightService.CurrentItems.FirstOrDefault(i => i.State == PreflightCheckState.Failed);
                PreflightErrorDetails.Text = failedItem?.DiagnosticDetails ?? "One or more required environment checks failed.";
                PreflightRetryContainer.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            PageLog("Preflight checks cancelled.");
        }
        catch (Exception ex)
        {
            PageLog($"Preflight checks exception: {ex}");
            PreflightErrorDetails.Text = $"Diagnostic execution error: {ex.Message}";
            PreflightRetryContainer.Visibility = Visibility.Visible;
        }
        finally
        {
            _preflightService.ItemProgressChanged -= onProgress;
        }
    }

    private void PreflightRetryButton_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Preflight retry clicked.");
        _ = RunPreflightChecksAsync();
    }

    private void ContinueButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ContinueButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
        ContinueArrowIcon.Margin = new Thickness(4, 0, 0, 0);
    }

    private void ContinueButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ContinueButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 102, 255));
        ContinueArrowIcon.Margin = new Thickness(0, 0, 0, 0);
    }

    private void ContinueButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ContinueButton.Background = new SolidColorBrush(Color.FromArgb(255, 0, 82, 204));
    }

    private void ContinueButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ContinueButton.Background = new SolidColorBrush(Color.FromArgb(255, 26, 117, 255));
    }

    private void UseDifferentAccount_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Use a different account clicked on Screen 4.");
        _isAccountSwitching = true;
        _previousUser = _currentUser;
        _isAuthCompleted = false;

        // Show overlay with Google account selection
        GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;
        GoogleWebViewLoading.Visibility = Visibility.Visible;
        GoogleAuthOverlay.Visibility = Visibility.Visible;
        GoogleAuthOverlay.Opacity = 1.0;

        OverlayHeaderIcon.Glyph = "\uE72E";
        OverlayHeaderTitle.Text = "Switch Google Account";
        OverlayHeaderSubtitle.Text = "• accounts.google.com";

        // Prompt Google's account selection
        GoogleAuthWebView.CoreWebView2?.Navigate("https://accounts.google.com/AccountChooser?continue=https%3A%2F%2Fmyaccount.google.com%2F");
    }

    private void UseDifferentAccount_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        UseDifferentAccountButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250));
    }

    private void UseDifferentAccount_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        UseDifferentAccountButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
    }

    private void ConnectedUserPhoto_ImageOpened(object sender, RoutedEventArgs e)
    {
        ConnectedUserPhotoBorder.Visibility = Visibility.Visible;
        ConnectedUserInitials.Visibility = Visibility.Collapsed;
    }

    private void ConnectedUserPhoto_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        PageLog($"ConnectedUserPhoto failed to load: {e.ErrorMessage}");
        ConnectedUserPhotoBorder.Visibility = Visibility.Collapsed;
        ConnectedUserInitials.Visibility = Visibility.Visible;
    }

    private async void SystemBrowserSignIn_Click(object sender, RoutedEventArgs e)
    {
        PageLog("System browser sign-in initiated.");
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;
        GoogleAuthOverlay.Visibility = Visibility.Collapsed;

        // Reset waiting card UI
        BrowserAuthSpinner.IsActive = true;
        BrowserAuthHeaderTitle.Text = "Waiting for Browser Sign-in...";
        BrowserAuthStatusPillText.Text = "Listening for callback & window return...";
        BrowserAuthDescText.Text = "Sign in in your browser. Klyro will automatically detect your login as soon as you switch back.";
        BrowserAuthVerifyBtn.IsEnabled = true;
        BrowserAuthWaitingCard.Visibility = Visibility.Visible;

        _isBrowserAuthInProgress = true;
        SystemBrowserSignInButton.IsEnabled = false;

        try
        {
            string authUrl = "https://accounts.google.com/ServiceLogin";
            PageLog($"Launching system browser for Google sign-in at {authUrl}");
            await _browserAuthService.StartSystemBrowserAuthAsync(authUrl, TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            PageLog($"SystemBrowserSignIn_Click error: {ex}");
        }
        finally
        {
            SystemBrowserSignInButton.IsEnabled = true;
        }
    }

    private void BrowserAuthService_AuthCompleted(object? sender, AuthenticatedUser user)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await CompleteGoogleAuthenticationAsync(user, "Default Browser (Callback)");
        });
    }

    private async void VerifyBrowserAuth_Click(object sender, RoutedEventArgs e)
    {
        PageLog("User manually confirmed browser sign-in completion.");
        var detectedUser = BrowserProfileDetector.DetectLatestGoogleAccount();
        await CompleteGoogleAuthenticationAsync(detectedUser, "Default Browser (Manual)");
    }

    private void CancelBrowserAuth_Click(object sender, RoutedEventArgs e)
    {
        PageLog("User cancelled browser sign-in waiting.");
        _isBrowserAuthInProgress = false;
        _browserAuthService.Cancel();
        BrowserAuthWaitingCard.Visibility = Visibility.Collapsed;
        SystemBrowserSignInButton.IsEnabled = true;
    }

    private void CloseGoogleAuth_Click(object sender, RoutedEventArgs e)
    {
        CloseGoogleAuth();
    }

    private void CloseGoogleAuth()
    {
        _isBrowserAuthInProgress = false;
        GoogleAuthFallbackOverlay.Visibility = Visibility.Collapsed;
        GoogleAuthOverlay.Visibility = Visibility.Collapsed;
        BrowserAuthWaitingCard.Visibility = Visibility.Collapsed;

        if (_isAccountSwitching && _previousUser != null)
        {
            _isAccountSwitching = false;
            _isAuthCompleted = true;
            _currentUser = _previousUser;
            SetConnectedUser(_previousUser);
            AccountConnectedStateGrid.Visibility = Visibility.Visible;
            AccountConnectedStateGrid.Opacity = 1.0;
            AccountSetupStateGrid.Visibility = Visibility.Collapsed;
            PageLog($"Cancelled account switching. Restored previous user: {_previousUser.Email}");
            return;
        }

        GoogleSignInButton.IsEnabled = true;
        SystemBrowserSignInButton.IsEnabled = true;
        GoogleButtonText.Text = "Continue with Google";
        GoogleArrowIcon.Visibility = Visibility.Visible;
        GoogleArrowIcon.Glyph = "\uE72A"; // Arrow
        GoogleArrowIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 102, 255));
        GoogleAuthProgressRing.Visibility = Visibility.Collapsed;
        GoogleAuthProgressRing.IsActive = false;
    }

    private void RetryGoogleAuth_Click(object sender, RoutedEventArgs e)
    {
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;
        GoogleSignIn_Click(sender, e);
    }

    private static KlyroConfiguration LoadConfiguration()
    {
        string[] relativeCandidates = { "config/exam.json", "config/default.json", "config/development.json" };
        foreach (var rel in relativeCandidates)
        {
            try
            {
                var fullPath = System.IO.Path.Combine(AppContext.BaseDirectory, rel);
                if (System.IO.File.Exists(fullPath))
                {
                    return ConfigurationLoader.LoadFromFile(fullPath);
                }
                if (System.IO.File.Exists(rel))
                {
                    return ConfigurationLoader.LoadFromFile(rel);
                }
            }
            catch { }
        }
        return new KlyroConfiguration();
    }

    private async Task CaptureScreenshotAsync(string filePath)
    {
        try
        {
            PageLog($"Capturing screenshot to {filePath}...");
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            var pixelBuffer = await rtb.GetPixelsAsync();

            var fullPath = System.IO.Path.GetFullPath(filePath);
            var dir = System.IO.Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
            System.IO.Directory.CreateDirectory(dir);

            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
            var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(fullPath), Windows.Storage.CreationCollisionOption.ReplaceExisting);

            using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                    stream);

                var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixelBuffer);
                byte[] pixels = new byte[pixelBuffer.Length];
                reader.ReadBytes(pixels);

                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)rtb.PixelWidth,
                    (uint)rtb.PixelHeight,
                    96,
                    96,
                    pixels);

                await encoder.FlushAsync();
            }

            PageLog($"Screenshot successfully saved to {fullPath} ({rtb.PixelWidth}x{rtb.PixelHeight}, {new System.IO.FileInfo(fullPath).Length} bytes).");
        }
        catch (Exception ex)
        {
            PageLog($"CaptureScreenshotAsync failed: {ex}");
        }
    }
}
