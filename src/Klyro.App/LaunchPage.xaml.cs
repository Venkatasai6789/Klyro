using System;
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
using Klyro.Core.Persistence;
using Klyro.Core.Startup;

namespace Klyro_App;

public sealed partial class LaunchPage : Page
{
    private const double MaxProgressBarWidth = 380.0;
    private readonly StartupPipeline _pipeline = new();
    private readonly OnboardingStateStore _onboardingStore = new();
    private CancellationTokenSource? _startupCts;
    private double _currentProgressWidth;
    private bool _isGoogleAuthInitialized = false;
    private bool _isAuthCompleted = false;

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
            InitializeComponent();
            PageLog("InitializeComponent succeeded.");
        }
        catch (Exception ex)
        {
            PageLog($"InitializeComponent failed: {ex}");
            throw;
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PageLog("Page_Loaded entered.");
        try
        {
            var cmdArgs = Environment.GetCommandLineArgs();

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
                for (int i = 0; i < cmdArgs.Length; i++)
                {
                    if ((cmdArgs[i].Equals("--screenshot-screen3", StringComparison.OrdinalIgnoreCase) ||
                         cmdArgs[i].Equals("--screenshot-account-setup", StringComparison.OrdinalIgnoreCase)) && i + 1 < cmdArgs.Length)
                    {
                        screenshotScreen3Path = cmdArgs[i + 1];
                    }
                }

                if (!string.IsNullOrEmpty(screenshotScreen3Path))
                {
                    await Task.Delay(400);
                    await CaptureScreenshotAsync(screenshotScreen3Path);
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

    private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
    {
        PageLog("Google Sign-In clicked.");
        GoogleAuthErrorCard.Visibility = Visibility.Collapsed;

        // Check network availability
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            GoogleAuthErrorCard.Visibility = Visibility.Visible;
            GoogleAuthErrorMessage.Text = "Unable to connect to Google. Check your internet connection and try again.";
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
            // Initialize WebView2 with persistent Exam profile
            if (!_isGoogleAuthInitialized)
            {
                var profileDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Klyro", "Profiles", "Exam");
                System.IO.Directory.CreateDirectory(profileDir);

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, profileDir, null);
                await GoogleAuthWebView.EnsureCoreWebView2Async(env);

                if (GoogleAuthWebView.CoreWebView2 != null)
                {
                    var settings = GoogleAuthWebView.CoreWebView2.Settings;
                    settings.AreDevToolsEnabled = false;
                    settings.AreDefaultContextMenusEnabled = false;
                    settings.AreBrowserAcceleratorKeysEnabled = false;

                    GoogleAuthWebView.CoreWebView2.NavigationStarting += GoogleAuthWebView_NavigationStarting;
                    GoogleAuthWebView.CoreWebView2.NavigationCompleted += GoogleAuthWebView_NavigationCompleted;
                    GoogleAuthWebView.CoreWebView2.SourceChanged += GoogleAuthWebView_SourceChanged;
                }
                _isGoogleAuthInitialized = true;
            }

            // Show authentication overlay
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
            GoogleAuthErrorMessage.Text = "Failed to launch Google sign-in. Please try again.";
        }
    }

    private void GoogleAuthWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        PageLog($"GoogleAuth navigating to: {e.Uri}");
    }

    private void GoogleAuthWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        GoogleWebViewLoading.Visibility = Visibility.Collapsed;
        CheckGoogleAuthCompletionAsync();
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
                _isAuthCompleted = true;
                PageLog("Google authentication successfully completed and verified!");

                // Hide overlay
                GoogleAuthOverlay.Visibility = Visibility.Collapsed;

                // Update button to success state
                GoogleButtonText.Text = "Google Account Connected ✓";
                GoogleSignInButton.Background = new SolidColorBrush(Color.FromArgb(255, 236, 253, 245)); // Emerald-50
                GoogleSignInButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)); // Emerald-500
                GoogleButtonText.Foreground = new SolidColorBrush(Color.FromArgb(255, 6, 95, 70)); // Emerald-800
                GoogleAuthProgressRing.Visibility = Visibility.Collapsed;
                GoogleAuthProgressRing.IsActive = false;
                GoogleArrowIcon.Visibility = Visibility.Visible;
                GoogleArrowIcon.Glyph = "\uE73E"; // Checkmark icon
                GoogleArrowIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));

                _onboardingStore.MarkCompleted("Google User", null);

                await Task.Delay(400);

                // Transition to assessment view in full-screen mode
                PageLog("Transitioning to assessment view...");
                if (MainWindow.CurrentInstance != null)
                {
                    MainWindow.CurrentInstance.NavigateToAssessmentView();
                }
                else
                {
                    Frame.Navigate(typeof(MainPage));
                }
            }
        }
        catch (Exception ex)
        {
            PageLog($"CheckGoogleAuthCompletionAsync error: {ex}");
        }
    }

    private void CloseGoogleAuth_Click(object sender, RoutedEventArgs e)
    {
        CloseGoogleAuth();
    }

    private void CloseGoogleAuth()
    {
        GoogleAuthOverlay.Visibility = Visibility.Collapsed;
        GoogleSignInButton.IsEnabled = true;
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
