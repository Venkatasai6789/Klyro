using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Klyro.Core.Browser;
using Klyro.Core.Configuration;
using Klyro.Core.Exam;
using Klyro.Core.Health;
using Klyro.Core.Logging;
using Klyro.Core.Network;
using Klyro.Core.Persistence;
using Klyro.Core.Recovery;
using Klyro.Core.Security;

namespace Klyro_App;

public sealed partial class MainPage : Page
{
    private KlyroConfiguration _config = new();
    private AuditLogger? _auditLogger;
    private AllowlistManager? _allowlistManager;
    private NavigationGuard? _navigationGuard;
    private FrameNavigationGuard? _frameGuard;
    private PopupGuard? _popupGuard;
    private DownloadGuard? _downloadGuard;
    private ProtocolGuard? _protocolGuard;
    private KeyboardHookManager? _keyboardHook;

    private SessionStore? _sessionStore;
    private NetworkMonitor? _networkMonitor;
    private RecoveryController? _recoveryController;
    private ExamStateMachine? _examStateMachine;

    private bool _isExamWebViewInitialized;
    private bool _isRecoveryWebViewInitialized;
    private CancellationTokenSource? _modalRecoveryPollCts;

    public bool IsAuthorizedToExit { get; set; }

    public MainPage()
    {
        InitializeComponent();
        KeyDown += MainPage_KeyDown;
    }

    private void MainPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (WifiPickerOverlay.Visibility == Visibility.Visible)
            {
                WifiPickerOverlay.Visibility = Visibility.Collapsed;
                WifiPasswordCard.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeSystemAsync();
        }
        catch (Exception ex)
        {
            // Prevent async void exception from causing WinUI stowed exception crash (0xc000027b)
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "boot.log"), $"[{DateTime.UtcNow:O}] [Page_Loaded FATAL] {ex}\n"); } catch { }
        }
    }

    private async Task InitializeSystemAsync()
    {
        void BootLog(string msg)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "boot.log"), $"[{DateTime.UtcNow:O}] [MainPage] {msg}\n"); } catch { }
        }

        try
        {
            BootLog("Initializing subsystems...");

            // 1. Load Configuration
            _config = LoadConfiguration();

            // 2. Initialize Audit Logger
            _auditLogger = new AuditLogger();
            _auditLogger.Log(EventType.EXAM_STARTED, "INITIALIZING", "Klyro shell launched.");

            // 3. Initialize Security & Keyboard Hooks
            _allowlistManager = new AllowlistManager(_config);
            _navigationGuard = new NavigationGuard(_allowlistManager);
            _frameGuard = new FrameNavigationGuard(_allowlistManager);
            _popupGuard = new PopupGuard(_allowlistManager, _config.Security);
            _downloadGuard = new DownloadGuard(_allowlistManager, _config.Security);
            _protocolGuard = new ProtocolGuard();

            _keyboardHook = new KeyboardHookManager();
            _keyboardHook.ExitRequested += (s, ev) => PromptForExitPin();
            _keyboardHook.InstallHook();

            // 4. Initialize Persistence & Recovery
            _sessionStore = new SessionStore();
            var existingSession = _sessionStore.LoadActiveSession();

            // 5. Initialize Network Engine & Autonomous Monitor
            _networkMonitor = new NetworkMonitor(_config, auditLogger: _auditLogger);
            _recoveryController = new RecoveryController(_networkMonitor, _config.Network, _auditLogger);

            _networkMonitor.StateChanged += OnNetworkStateChanged;
            _networkMonitor.ProbeCompleted += OnNetworkProbeCompleted;
            _recoveryController.StateChanged += OnRecoveryStateChanged;
            _recoveryController.ExamResumeRequested += OnExamResumeRequested;

            _networkMonitor.Start();
            BootLog("Network engine started.");

            // 5.5 Autonomous Campus Wi-Fi Auto-Connect (KLU_WIFI or lab APs)
            _ = Task.Run(async () =>
            {
                try
                {
                    await WifiNetworkManager.AutoConnectCampusWifiAsync();
                }
                catch { }
            });

            // 6. Initialize Exam State Machine
            _examStateMachine = new ExamStateMachine(_config, _sessionStore, _auditLogger, existingSession);

            // Show dev simulation toggle if configured
            if (_config.Network.EnableNetworkSimulator || string.Equals(_config.Application.Environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                SimulatorToggleButton.Visibility = Visibility.Visible;
            }

            // 7. Execute Pre-flight Checks
            await RunPreflightValidationAsync();
            BootLog("All subsystems initialized. Preflight complete. Application ready.");
        }
        catch (Exception ex)
        {
            BootLog($"[FATAL] InitializeSystemAsync: {ex}");
            ShowPreflightError($"Fatal startup error: {ex.Message}");
        }
    }

    private async Task RunPreflightValidationAsync()
    {
        PreflightProgressBar.IsIndeterminate = true;
        PreflightErrorMessage.Visibility = Visibility.Collapsed;
        PreflightRetryButton.Visibility = Visibility.Collapsed;
        PreflightWifiButton.Visibility = Visibility.Collapsed;

        var checker = PreflightChecker.CreateDefault(_config, auditLogger: _auditLogger);
        var report = await checker.RunAllChecksAsync();

        PreflightProgressBar.IsIndeterminate = false;

        if (report.CanStartExam)
        {
            PfCheckSystem.Text = "✓ System security and operating system policy verified";
            PfCheckSystem.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));

            PfCheckConfig.Text = "✓ Examination configuration integrity verified";
            PfCheckConfig.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));

            PfCheckNetwork.Text = "✓ Network adapter and campus gateway reachable";
            PfCheckNetwork.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));

            PfCheckAssessment.Text = "✓ Assessment platform verified";
            PfCheckAssessment.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));

            await Task.Delay(400);
            PreflightOverlay.Visibility = Visibility.Collapsed;

            try
            {
                await InitializeExamWebViewAsync();
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(EventType.SECURITY_VIOLATION, "EXAM_WEBVIEW_ERROR", $"Failed to initialize ExamWebView: {ex.Message} | Stack: {ex.StackTrace}");
                ShowNotice($"Browser initialization failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        else
        {
            PreflightWifiButton.Visibility = Visibility.Visible;
            ShowPreflightError("One or more critical pre-flight security checks failed. Please connect to campus Wi-Fi or contact the invigilator.");
        }
    }

    private void ShowPreflightError(string message)
    {
        PreflightProgressBar.IsIndeterminate = false;
        PreflightErrorMessage.Text = message;
        PreflightErrorMessage.Visibility = Visibility.Visible;
        PreflightRetryButton.Visibility = Visibility.Visible;
        PreflightWifiButton.Visibility = Visibility.Visible;
    }

    private async void PreflightRetry_Click(object sender, RoutedEventArgs e)
    {
        await RunPreflightValidationAsync();
    }

    private async Task InitializeExamWebViewAsync()
    {
        if (_isExamWebViewInitialized) return;

        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", "Exam");
        Directory.CreateDirectory(profileDir);

        Microsoft.Web.WebView2.Core.CoreWebView2Environment? environment = null;
        try
        {
            environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, profileDir, null);
            await ExamWebView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exInit)
        {
            _auditLogger?.Log(EventType.WEBVIEW_CRASH, "WEBVIEW_FALLBACK", $"Primary profile init failed: {exInit.Message}. Using isolated session directory.");
            var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", $"Exam_{Environment.ProcessId}");
            Directory.CreateDirectory(fallbackDir);
            environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackDir, null);
            await ExamWebView.EnsureCoreWebView2Async(environment);
        }

        if (ExamWebView.CoreWebView2 == null)
        {
            throw new InvalidOperationException("CoreWebView2 runtime could not be initialized on ExamWebView.");
        }

        var settings = ExamWebView.CoreWebView2.Settings;
        if (settings != null)
        {
            settings.AreDevToolsEnabled = _config?.Security?.AllowDevTools ?? false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
        }

        // Block shortcut keys: Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+P, Ctrl+S, Ctrl+U, F12
        ExamWebView.PreviewKeyDown += (s, args) =>
        {
            var key = args.Key;
            bool isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (isCtrl)
            {
                if (key is Windows.System.VirtualKey.C or Windows.System.VirtualKey.V or Windows.System.VirtualKey.X or Windows.System.VirtualKey.P or Windows.System.VirtualKey.S or Windows.System.VirtualKey.U)
                {
                    args.Handled = true;
                }
            }
            if (key == Windows.System.VirtualKey.F12)
            {
                args.Handled = true;
            }
        };

        // Suppress right-click context menu and clipboard events inside web content
        await ExamWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            "document.addEventListener('contextmenu', e => e.preventDefault(), true);" +
            "document.addEventListener('copy', e => e.preventDefault(), true);" +
            "document.addEventListener('cut', e => e.preventDefault(), true);" +
            "document.addEventListener('paste', e => e.preventDefault(), true);");

        // Attach strict security guards
        ExamWebView.CoreWebView2.NavigationStarting += ExamWebView_NavigationStarting;
        ExamWebView.CoreWebView2.NavigationCompleted += ExamWebView_NavigationCompleted;
        ExamWebView.CoreWebView2.SourceChanged += ExamWebView_SourceChanged;
        ExamWebView.CoreWebView2.FrameNavigationStarting += ExamWebView_FrameNavigationStarting;
        ExamWebView.CoreWebView2.NewWindowRequested += ExamWebView_NewWindowRequested;
        ExamWebView.CoreWebView2.DownloadStarting += ExamWebView_DownloadStarting;
        ExamWebView.CoreWebView2.ProcessFailed += (s, e) =>
        {
            _auditLogger?.Log(EventType.WEBVIEW_CRASH, "PROCESS_FAILED", $"Exam WebView2 process failure: {e.ProcessFailedKind}, reason: {e.Reason}");
            if (e.ProcessFailedKind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                try { ExamWebView.CoreWebView2?.Reload(); } catch { }
            }
        };

        _isExamWebViewInitialized = true;

        var targetUrl = _examStateMachine?.CurrentSession?.LastKnownAssessmentUrl ?? _config?.Exam?.StartUrl ?? "https://www.codechef.com/";
        ExamWebView.CoreWebView2.Navigate(targetUrl);

        _examStateMachine?.TransitionTo(ExamState.ACTIVE);
    }

    private void ExamWebView_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        PageLoadingProgressBar.Visibility = Visibility.Visible;

        var decision = _navigationGuard?.EvaluateExamNavigation(e.Uri);
        if (decision is { IsAllowed: false })
        {
            e.Cancel = true;
            PageLoadingProgressBar.Visibility = Visibility.Collapsed;
            _auditLogger?.Log(EventType.NAVIGATION_BLOCKED, "ACTIVE", decision.Reason, e.Uri);
            ShowNotice($"Navigation blocked: {decision.Reason}", InfoBarSeverity.Error);
        }
    }

    private void ExamWebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        PageLoadingProgressBar.Visibility = Visibility.Collapsed;
        NavBackButton.IsEnabled = ExamWebView.CanGoBack;
        NavForwardButton.IsEnabled = ExamWebView.CanGoForward;

        if (!e.IsSuccess && e.WebErrorStatus != Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled)
        {
            ExamErrorMessage.Text = $"Unable to reach assessment platform (Status: {e.WebErrorStatus}). Please ensure your campus Wi-Fi authentication is active.";
            ExamErrorOverlay.Visibility = Visibility.Visible;
        }
        else if (e.IsSuccess)
        {
            ExamErrorOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ExamWebView_SourceChanged(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
    {
        CurrentUrlTextBlock.Text = ExamWebView.Source?.ToString() ?? _config.Exam.StartUrl;
        NavBackButton.IsEnabled = ExamWebView.CanGoBack;
        NavForwardButton.IsEnabled = ExamWebView.CanGoForward;
    }

    private void ExamWebView_FrameNavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = _frameGuard?.EvaluateFrameNavigation(e.Uri);
        if (decision is { IsAllowed: false })
        {
            e.Cancel = true;
            _auditLogger?.Log(EventType.NAVIGATION_BLOCKED, "ACTIVE", $"Iframe blocked: {decision.Reason}", e.Uri);
        }
    }

    private void ExamWebView_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _auditLogger?.Log(EventType.POPUP_BLOCKED, "ACTIVE", "Popup or new window request suppressed.", e.Uri);
    }

    private void ExamWebView_DownloadStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs e)
    {
        var decision = _downloadGuard?.EvaluateDownloadRequest(e.DownloadOperation.Uri);
        if (decision is { IsAllowed: false })
        {
            e.Handled = true;
            e.Cancel = true;
            _auditLogger?.Log(EventType.DOWNLOAD_BLOCKED, "ACTIVE", decision.Reason, e.DownloadOperation.Uri);
            ShowNotice("Downloads are prohibited during this assessment.", InfoBarSeverity.Warning);
        }
    }

    private void OnNetworkStateChanged(NetworkState previousState, NetworkState newState, ProbeMatrix matrix)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                UpdateNetworkQualityDisplay(matrix);

                switch (newState)
                {
                    case NetworkState.ONLINE:
                        NoticeBar.IsOpen = false;
                        ExamErrorOverlay.Visibility = Visibility.Collapsed;

                        // If recovery popup is currently visible, verify and smoothly minimize to background
                        if (RecoveryModalOverlay.Opacity > 0)
                        {
                            RecoveryStatusPanel.Visibility = Visibility.Visible;
                            RecoveryStatusText.Text = "✓ Connection verified! Keeping Wi-Fi session active in background...";
                            await Task.Delay(800);
                            KeepRecoveryActiveInBackground();
                            ExamWebView.Focus(FocusState.Programmatic);
                            ShowNotice("Campus Wi-Fi session active in background. Wi-Fi connection preserved.", InfoBarSeverity.Success);
                        }
                        break;

                    case NetworkState.DEGRADED:
                        break;

                    case NetworkState.PORTAL_REQUIRED:
                        // Mid-exam automatic popup: prompt student immediately without tab sifting
                        ShowRecoveryModal(forceNavigate: true);
                        ShowNotice("Campus Wi-Fi session expired. Please sign in below to reconnect.", InfoBarSeverity.Warning);
                        break;

                    case NetworkState.OFFLINE:
                        ShowNotice("Campus Wi-Fi connection lost. Searching for campus signal...", InfoBarSeverity.Warning);
                        _ = Task.Run(async () =>
                        {
                            try { await WifiNetworkManager.AutoConnectCampusWifiAsync(); } catch { }
                        });
                        // Automatic popup to reconnect when internet is lost
                        ShowRecoveryModal(forceNavigate: false);
                        break;

                    case NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE:
                        ShowNotice("Assessment service temporarily unavailable. Your session is preserved.", InfoBarSeverity.Warning);
                        break;
                }
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(EventType.SECURITY_VIOLATION, "NETWORK_STATE_DISPATCH_ERROR", ex.Message);
            }
        });
    }

    private void OnNetworkProbeCompleted(object? sender, ProbeMatrix matrix)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                UpdateNetworkQualityDisplay(matrix);
            }
            catch { }
        });
    }

    private void UpdateNetworkQualityDisplay(ProbeMatrix matrix)
    {
        try
        {
            if (WifiSignalTextBlock == null || PingTextBlock == null || WifiSignalIcon == null || NetworkQualityBadge == null)
            {
                return;
            }

            var wifiInfo = WifiSignalService.GetCurrentWifiSignal();
            if (wifiInfo.signalPercentage > 0)
            {
                WifiSignalTextBlock.Text = $"{wifiInfo.signalPercentage}%";
            }
            else if (_networkMonitor?.CurrentState == NetworkState.ONLINE)
            {
                WifiSignalTextBlock.Text = "Wi-Fi";
            }
            else
            {
                WifiSignalTextBlock.Text = "--%";
            }

            if (_networkMonitor?.CurrentState == NetworkState.PORTAL_REQUIRED)
            {
                PingTextBlock.Text = "Login Needed";
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235)); // #2563EB
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 191, 219, 254)); // #BFDBFE
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 239, 246, 255)); // #EFF6FF

                // Tab Badge
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "Login Needed";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 239, 246, 255));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 191, 219, 254));
                }
                return;
            }

            if (_networkMonitor?.CurrentState == NetworkState.OFFLINE)
            {
                PingTextBlock.Text = "No Internet";
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)); // #DC2626
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 254, 202, 202)); // #FECACA
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 254, 242, 242)); // #FEF2F2

                // Tab Badge
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "No Internet";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 254, 242, 242));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 254, 202, 202));
                }
                return;
            }

            long latency = matrix.Internet.LatencyMs > 0 ? matrix.Internet.LatencyMs : matrix.Gateway.LatencyMs;
            string tier = _networkMonitor?.ConnectionSpeedTier ?? (latency < 80 ? "Fast" : latency < 250 ? "Good" : "Slow");

            if (latency > 0)
            {
                PingTextBlock.Text = $"{latency} ms • {tier}";
                if (latency < 80)
                {
                    // Optimal (Green #16A34A)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240));
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));
                }
                else if (latency < 250)
                {
                    // Moderate (Amber #D97706)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 253, 230, 138));
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 255, 251, 235));
                }
                else
                {
                    // High latency (Orange #EA580C)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 234, 88, 12));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 234, 88, 12));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 254, 215, 170));
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 255, 247, 237));
                }

                // Tab Badge: Active in BG
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = $"Active in BG ({latency}ms)";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 21, 128, 61));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 220, 252, 231));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172));
                }
            }
            else
            {
                PingTextBlock.Text = "Online";
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 226, 232, 240));
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 248, 250, 252));

                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "Active in BG";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 21, 128, 61));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 220, 252, 231));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172));
                }
            }
        }
        catch { }
    }

    private void OnRecoveryStateChanged(RecoveryState previousState, RecoveryState newState)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                switch (newState)
                {
                    case RecoveryState.LoginRequired:
                        ShowRecoveryModal(forceNavigate: true);
                        break;

                    case RecoveryState.Authenticating:
                        RecoveryStatusPanel.Visibility = Visibility.Visible;
                        RecoveryStatusText.Text = "Authenticating... verifying Internet connection";
                        break;

                    case RecoveryState.ConnectionRestored:
                        RecoveryStatusPanel.Visibility = Visibility.Visible;
                        RecoveryStatusText.Text = "✓ Connection restored! Preserving Wi-Fi session in background...";
                        await Task.Delay(800);
                        KeepRecoveryActiveInBackground();
                        ExamWebView.Focus(FocusState.Programmatic);
                        ShowNotice("Campus Wi-Fi authenticated. Assessment connection restored.", InfoBarSeverity.Success);
                        break;

                    case RecoveryState.RecoveryFailed:
                        RecoveryStatusPanel.Visibility = Visibility.Collapsed;
                        ShowNotice("Portal authentication did not restore connection. Please re-enter credentials or reconnect Wi-Fi.", InfoBarSeverity.Error);
                        break;
                }
            }
            catch { }
        });
    }

    private void ShowRecoveryModal(bool forceNavigate = false)
    {
        RecoveryModalOverlay.Opacity = 1;
        RecoveryModalOverlay.IsHitTestVisible = true;
        Canvas.SetZIndex(RecoveryModalOverlay, 35);
        RecoveryStatusPanel.Visibility = Visibility.Collapsed;

        if (_networkMonitor?.CurrentState is NetworkState.PORTAL_REQUIRED or NetworkState.OFFLINE)
        {
            RecoveryModalTitle.Text = "RECONNECT INTERNET OR LOGIN TO CAMPUS WI-FI (KLU_WIFI)";
            RecoveryModalSubtitle.Text = "No internet connection detected. Please sign in below to restore access. Your exam progress and code are safely preserved.";
        }
        else
        {
            RecoveryModalTitle.Text = "CAMPUS WI-FI SESSION (KLU_WIFI)";
            RecoveryModalSubtitle.Text = "Campus Wi-Fi session is active in the background. Return to CodeChef whenever ready.";
        }

        UpdateTabHeaderState(isWifiTabActive: true);
        _ = InitializeRecoveryWebViewAsync(forceNavigate);
        StartModalConnectivityPolling();
    }

    private void KeepRecoveryActiveInBackground()
    {
        _modalRecoveryPollCts?.Cancel();
        RecoveryModalOverlay.Opacity = 0;
        RecoveryModalOverlay.IsHitTestVisible = false;
        Canvas.SetZIndex(RecoveryModalOverlay, 0);

        UpdateTabHeaderState(isWifiTabActive: false);
        _recoveryController?.KeepActiveInBackground();
    }

    private void UpdateTabHeaderState(bool isWifiTabActive)
    {
        if (isWifiTabActive)
        {
            TabWifiPortalButton.Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            TabWifiPortalButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 147, 197, 253));
            TabWifiPortalButton.BorderThickness = new Thickness(1);
            TabWifiTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));

            TabAssessmentButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabAssessmentButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabAssessmentButton.BorderThickness = new Thickness(0);
            TabAssessmentText.Foreground = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
        }
        else
        {
            TabAssessmentButton.Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            TabAssessmentButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 147, 197, 253));
            TabAssessmentButton.BorderThickness = new Thickness(1);
            TabAssessmentText.Foreground = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));

            TabWifiPortalButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabWifiPortalButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabWifiPortalButton.BorderThickness = new Thickness(0);
            TabWifiTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 71, 85, 105));
        }
    }

    private async Task InitializeRecoveryWebViewAsync(bool forceNavigate = false)
    {
        if (!_isRecoveryWebViewInitialized)
        {
            var recoveryProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", "Recovery");
            Directory.CreateDirectory(recoveryProfileDir);
            var envOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion"
            };

            Microsoft.Web.WebView2.Core.CoreWebView2Environment? environment = null;
            try
            {
                environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, recoveryProfileDir, envOptions);
                await RecoveryWebView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception exInit)
            {
                _auditLogger?.Log(EventType.WEBVIEW_CRASH, "RECOVERY_WEBVIEW_FALLBACK", $"Primary recovery profile failed: {exInit.Message}. Using isolated directory.");
                var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", $"Recovery_{Environment.ProcessId}");
                Directory.CreateDirectory(fallbackDir);
                environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackDir, envOptions);
                await RecoveryWebView.EnsureCoreWebView2Async(environment);
            }

            if (RecoveryWebView.CoreWebView2 != null)
            {
                var settings = RecoveryWebView.CoreWebView2.Settings;
                if (settings != null)
                {
                    settings.AreDevToolsEnabled = false;
                    settings.AreDefaultContextMenusEnabled = false;
                }

                // Strict recovery allowlist guard
                RecoveryWebView.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    var decision = _navigationGuard?.EvaluateRecoveryNavigation(e.Uri);
                    if (decision is { IsAllowed: false })
                    {
                        e.Cancel = true;
                        _auditLogger?.Log(EventType.NAVIGATION_BLOCKED, "PORTAL_RECOVERY", $"Recovery navigation blocked: {decision.Reason}", e.Uri);
                    }
                };

                RecoveryWebView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;

                RecoveryWebView.CoreWebView2.ProcessFailed += (s, e) =>
                {
                    _auditLogger?.Log(EventType.WEBVIEW_CRASH, "RECOVERY_PROCESS_FAILED", $"Recovery WebView2 process failure: {e.ProcessFailedKind}");
                    try { RecoveryWebView.CoreWebView2?.Reload(); } catch { }
                };

                // Monitor navigation completion to immediately trigger connectivity probe
                RecoveryWebView.CoreWebView2.NavigationCompleted += async (s, e) =>
                {
                    if (e.IsSuccess && _recoveryController != null)
                    {
                        await _recoveryController.HandleCredentialsSubmittedAsync();
                    }
                };

                _isRecoveryWebViewInitialized = true;
            }
        }

        if (forceNavigate || RecoveryWebView.Source == null)
        {
            var portalUrl = _config?.Network?.PortalUrl ?? "http://172.16.103.254:8090/";
            RecoveryWebView.CoreWebView2?.Navigate(portalUrl);
        }
    }

    private void StartModalConnectivityPolling()
    {
        _modalRecoveryPollCts?.Cancel();
        _modalRecoveryPollCts = new CancellationTokenSource();
        var token = _modalRecoveryPollCts.Token;

        Task.Run(async () =>
        {
            using var probe = new ConnectivityProbe();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1500, token);
                    var result = await probe.ExecuteAsync(ProbeType.InternetConnectivity, "http://clients3.google.com/generate_204", 1200, token);
                    if (result.IsSuccess)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            KeepRecoveryActiveInBackground();
                            WifiPortalButton.Visibility = Visibility.Collapsed;
                            ExamErrorOverlay.Visibility = Visibility.Collapsed;

                            if (_isExamWebViewInitialized)
                            {
                                ExamWebView.Reload();
                            }
                        });
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Continue polling
                }
            }
        }, token);
    }

    private void OnExamResumeRequested(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ExamWebView.Focus(FocusState.Programmatic);
            _examStateMachine?.TransitionTo(ExamState.ACTIVE);
            ShowNotice("Connection verified. Examination resumed.", InfoBarSeverity.Success);
            if (ExamErrorOverlay.Visibility == Visibility.Visible)
            {
                ExamErrorOverlay.Visibility = Visibility.Collapsed;
                ExamWebView.Reload();
            }
        });
    }

    private async void RecoveryRetry_Click(object sender, RoutedEventArgs e)
    {
        RecoveryStatusPanel.Visibility = Visibility.Visible;
        RecoveryStatusText.Text = "Verifying Internet connection...";
        var state = await _networkMonitor!.TriggerImmediateProbeAsync();
        if (state == NetworkState.ONLINE)
        {
            RecoveryStatusText.Text = "✓ Connection Restored! Keeping session active in background...";
            await Task.Delay(600);
            KeepRecoveryActiveInBackground();
            ExamErrorOverlay.Visibility = Visibility.Collapsed;
            ExamWebView.Reload();
            ShowNotice("Internet restored. Campus Wi-Fi session running in background.", InfoBarSeverity.Success);
        }
        else
        {
            RecoveryStatusText.Text = "Internet not yet accessible. Please sign in above or check connection.";
        }
    }

    private void RecoveryReload_Click(object sender, RoutedEventArgs e)
    {
        RecoveryWebView.CoreWebView2?.Navigate(_config.Network.PortalUrl);
    }

    private void RecoveryDismiss_Click(object sender, RoutedEventArgs e)
    {
        KeepRecoveryActiveInBackground();
        ShowNotice("Campus Wi-Fi session is active in the background. Exam resumed.", InfoBarSeverity.Informational);
    }

    // Tab Navigation Handlers
    private void TabAssessment_Click(object sender, RoutedEventArgs e)
    {
        KeepRecoveryActiveInBackground();
        ExamWebView.Focus(FocusState.Programmatic);
    }

    private void TabWifiPortal_Click(object sender, RoutedEventArgs e)
    {
        ShowRecoveryModal(forceNavigate: false);
    }

    private void WifiPortalButton_Click(object sender, RoutedEventArgs e)
    {
        ShowRecoveryModal(forceNavigate: false);
    }

    // Wi-Fi Network Picker Handlers
    private WifiNetworkItem? _selectedNetworkForPassword;

    private void NetworkQualityButton_Click(object sender, RoutedEventArgs e)
    {
        OpenWifiPicker();
    }

    private void WifiPickerOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenWifiPicker();
    }

    private void WifiPickerClose_Click(object sender, RoutedEventArgs e)
    {
        WifiPickerOverlay.Visibility = Visibility.Collapsed;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
    }

    private void WifiPickerOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        WifiPickerOverlay.Visibility = Visibility.Collapsed;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
    }

    private void DialogContent_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true; // Prevent click inside dialog card from dismissing modal
    }

    private async void WifiRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWifiListAsync();
    }

    private void OpenWifiPicker()
    {
        WifiPickerOverlay.Visibility = Visibility.Visible;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
        WifiConnectingBanner.Visibility = Visibility.Collapsed;
        _ = RefreshWifiListAsync();
    }

    private async Task RefreshWifiListAsync()
    {
        WifiRefreshButton.IsEnabled = false;
        WifiNetworksListPanel.Children.Clear();

        var loadingRing = new ProgressRing
        {
            Width = 24,
            Height = 24,
            IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24)
        };
        WifiNetworksListPanel.Children.Add(loadingRing);

        var networks = await WifiNetworkManager.ScanNetworksAsync();

        WifiNetworksListPanel.Children.Clear();
        WifiRefreshButton.IsEnabled = true;

        if (networks.Count == 0)
        {
            WifiNetworksListPanel.Children.Add(new TextBlock
            {
                Text = "No Wi-Fi networks found. Check your wireless adapter.",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16)
            });
            return;
        }

        foreach (var net in networks)
        {
            WifiNetworksListPanel.Children.Add(BuildWifiNetworkCard(net));
        }
    }

    private Border BuildWifiNetworkCard(WifiNetworkItem net)
    {
        var card = new Border
        {
            Background = net.IsConnected ? new SolidColorBrush(Color.FromArgb(255, 239, 246, 255)) : new SolidColorBrush(Color.FromArgb(255, 248, 250, 252)),
            BorderBrush = net.IsConnected ? new SolidColorBrush(Color.FromArgb(255, 147, 197, 253)) : new SolidColorBrush(Color.FromArgb(255, 226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE701",
            FontSize = 14,
            Foreground = net.IsCampusNetwork ? new SolidColorBrush(Color.FromArgb(255, 22, 163, 74)) : new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(new TextBlock
        {
            Text = net.Ssid,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42))
        });

        if (net.IsCampusNetwork)
        {
            var campusBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 220, 252, 231)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1)
            };
            campusBadge.Child = new TextBlock
            {
                Text = "Campus Wi-Fi",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 21, 128, 61))
            };
            topRow.Children.Add(campusBadge);
        }

        if (net.IsConnected)
        {
            var connectedBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 219, 234, 254)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 147, 197, 253)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1)
            };
            connectedBadge.Child = new TextBlock
            {
                Text = "Connected",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216))
            };
            topRow.Children.Add(connectedBadge);
        }

        if (!net.IsOpen)
        {
            topRow.Children.Add(new FontIcon
            {
                Glyph = "\uE72E",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        details.Children.Add(topRow);

        var subText = new TextBlock
        {
            Text = $"{net.SignalPercent}% signal \u2022 {net.SecurityDisplay}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139))
        };
        details.Children.Add(subText);

        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        if (net.IsConnected)
        {
            var activeText = new TextBlock
            {
                Text = "\u2713 Active",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(activeText, 2);
            grid.Children.Add(activeText);
        }
        else
        {
            var connectBtn = new Button
            {
                Content = "Connect",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (net.IsCampusNetwork)
            {
                connectBtn.Background = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
                connectBtn.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            }
            connectBtn.Click += (s, e) => InitiateWifiConnection(net);

            Grid.SetColumn(connectBtn, 2);
            grid.Children.Add(connectBtn);
        }

        card.Child = grid;
        return card;
    }

    private void InitiateWifiConnection(WifiNetworkItem net)
    {
        if (!net.IsOpen && !net.HasProfile)
        {
            _selectedNetworkForPassword = net;
            WifiPasswordPromptText.Text = $"Enter password for \"{net.Ssid}\":";
            WifiPasswordInput.Password = string.Empty;
            WifiPasswordCard.Visibility = Visibility.Visible;
            WifiPasswordInput.Focus(FocusState.Programmatic);
            return;
        }

        _ = ExecuteWifiConnectAsync(net.Ssid);
    }

    private void WifiPasswordCancel_Click(object sender, RoutedEventArgs e)
    {
        WifiPasswordCard.Visibility = Visibility.Collapsed;
        _selectedNetworkForPassword = null;
    }

    private void WifiPasswordSubmit_Click(object sender, RoutedEventArgs e)
    {
        SubmitWifiPassword();
    }

    private void WifiPasswordInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SubmitWifiPassword();
        }
    }

    private void SubmitWifiPassword()
    {
        if (_selectedNetworkForPassword == null) return;

        string pwd = WifiPasswordInput.Password?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pwd)) return;

        var target = _selectedNetworkForPassword.Ssid;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
        _ = ExecuteWifiConnectAsync(target, pwd);
    }

    private async Task ExecuteWifiConnectAsync(string ssid, string? password = null)
    {
        WifiConnectingBanner.Visibility = Visibility.Visible;
        WifiConnectingText.Text = $"Connecting to {ssid}...";

        bool success = await WifiNetworkManager.ConnectToNetworkAsync(ssid, password);

        if (success)
        {
            WifiConnectingText.Text = $"\u2713 Associated with {ssid}. Verifying connection...";
            await Task.Delay(800);
            WifiConnectingBanner.Visibility = Visibility.Collapsed;
            WifiPickerOverlay.Visibility = Visibility.Collapsed;

            ShowNotice($"Connected to {ssid}.", InfoBarSeverity.Success);
            _ = RefreshWifiListAsync();

            if (_networkMonitor != null)
            {
                await _networkMonitor.TriggerImmediateProbeAsync();
            }
        }
        else
        {
            WifiConnectingBanner.Visibility = Visibility.Collapsed;
            ShowNotice($"Failed to connect to {ssid}. Please check signal and credentials.", InfoBarSeverity.Error);
            await RefreshWifiListAsync();
        }
    }

    // Navigation Toolbar Event Handlers
    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        if (ExamWebView.CanGoBack) ExamWebView.GoBack();
    }

    private void NavForward_Click(object sender, RoutedEventArgs e)
    {
        if (ExamWebView.CanGoForward) ExamWebView.GoForward();
    }

    private void NavRefresh_Click(object sender, RoutedEventArgs e)
    {
        ExamErrorOverlay.Visibility = Visibility.Collapsed;
        ExamWebView.Reload();
    }

    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        ExamErrorOverlay.Visibility = Visibility.Collapsed;
        ExamWebView.CoreWebView2?.Navigate(_config.Exam.StartUrl);
    }

    // Invigilator Exit PIN Dialog Handlers
    private void ExitExamButton_Click(object sender, RoutedEventArgs e)
    {
        PromptForExitPin();
    }

    public void PromptForExitPin()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PinErrorMessage.Visibility = Visibility.Collapsed;
            ExitPinPasswordBox.Password = string.Empty;
            PinModalOverlay.Visibility = Visibility.Visible;
            ExitPinPasswordBox.Focus(FocusState.Programmatic);
        });
    }

    private void AuthorizePin_Click(object sender, RoutedEventArgs e)
    {
        ValidateAndExit();
    }

    private void ExitPinPasswordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ValidateAndExit();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelPin();
            e.Handled = true;
        }
    }

    private void ValidateAndExit()
    {
        var enteredPin = ExitPinPasswordBox.Password?.Trim();
        var configuredPin = _config?.Security?.ExitPin;
        var expectedPin = !string.IsNullOrWhiteSpace(configuredPin) ? configuredPin : "123456";

        // Accept 123456 as well as any configured PIN
        if (string.Equals(enteredPin, expectedPin, StringComparison.Ordinal) ||
            string.Equals(enteredPin, "123456", StringComparison.Ordinal))
        {
            IsAuthorizedToExit = true;

            // 1. Immediately dismiss overlay to unblock screen visually
            PinModalOverlay.Visibility = Visibility.Collapsed;

            // 2. Log exit to boot log & audit logger
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "boot.log"), $"[{DateTime.UtcNow:O}] [MainPage] Invigilator PIN verified. Shell exiting...\n"); } catch { }
            try { _auditLogger?.Log(EventType.EXAM_COMPLETED, "EXIT", "Invigilator PIN verified. Shell exiting."); } catch { }

            // 3. Uninstall unmanaged low-level keyboard hook immediately
            try { _keyboardHook?.UninstallHook(); } catch { }

            // 4. Dispose background network engine & audit logger
            try { _networkMonitor?.Stop(); } catch { }
            try { _networkMonitor?.Dispose(); } catch { }
            try { _auditLogger?.Dispose(); } catch { }

            // 5. Completely terminate application and process tree (guarantees no background zombie remains)
            App.ExitCompletely();
        }
        else
        {
            PinErrorMessage.Visibility = Visibility.Visible;
            ExitPinPasswordBox.SelectAll();
            _auditLogger?.Log(EventType.SECURITY_VIOLATION, "PIN_REJECTED", "Invalid invigilator PIN entered.");
        }
    }

    public void CancelPin()
    {
        PinModalOverlay.Visibility = Visibility.Collapsed;
        PinErrorMessage.Visibility = Visibility.Collapsed;
        ExitPinPasswordBox.Password = string.Empty;
    }

    private void CancelPin_Click(object sender, RoutedEventArgs e)
    {
        CancelPin();
    }

    private void PinModalOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        CancelPin();
    }

    private void PinModalOverlay_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelPin();
            e.Handled = true;
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
    }

    // Developer Simulator Actions
    private void SimulatorToggle_Click(object sender, RoutedEventArgs e)
    {
        SimulatorDrawer.Visibility = SimulatorDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SimNormal_Click(object sender, RoutedEventArgs e) =>
        _networkMonitor!.Simulator.CurrentCondition = SimulatedNetworkCondition.Normal;

    private void SimPortalReq_Click(object sender, RoutedEventArgs e) =>
        _networkMonitor!.Simulator.CurrentCondition = SimulatedNetworkCondition.PortalRequired;

    private void SimDegraded_Click(object sender, RoutedEventArgs e) =>
        _networkMonitor!.Simulator.CurrentCondition = SimulatedNetworkCondition.Degraded;

    private void SimOffline_Click(object sender, RoutedEventArgs e) =>
        _networkMonitor!.Simulator.CurrentCondition = SimulatedNetworkCondition.Offline;

    private void SimChefDown_Click(object sender, RoutedEventArgs e) =>
        _networkMonitor!.Simulator.CurrentCondition = SimulatedNetworkCondition.CodeChefUnavailable;

    private static KlyroConfiguration LoadConfiguration()
    {
        string[] relativeCandidates = { "config/exam.json", "config/default.json", "config/development.json" };
        foreach (var rel in relativeCandidates)
        {
            try
            {
                var fullPath = Path.Combine(AppContext.BaseDirectory, rel);
                if (File.Exists(fullPath))
                {
                    return ConfigurationLoader.LoadFromFile(fullPath);
                }
                if (File.Exists(rel))
                {
                    return ConfigurationLoader.LoadFromFile(rel);
                }
            }
            catch (Exception ex)
            {
                string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
                try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [Config Load Warning] Failed to load {rel}: {ex.Message}. Falling back.\n"); } catch { }
            }
        }
        return new KlyroConfiguration();
    }
}
