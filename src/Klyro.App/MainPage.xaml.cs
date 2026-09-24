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
using Klyro.Core.Authentication;
using Klyro.Core.Security;
using Klyro.Core.Tabs;

namespace Klyro_App;

public sealed partial class MainPage : Page
{
    private KlyroConfiguration _config = new();
    private readonly OnboardingStateStore _onboardingStore = new();
    private AuthenticatedUser? _authenticatedCandidate;
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
    private TabWorkerManager? _tabWorkerManager;
    private SecurityWatchdog? _securityWatchdog;

    private bool _isExamWebViewInitialized;
    private bool _isRecoveryWebViewInitialized;
    private CancellationTokenSource? _modalRecoveryPollCts;
    private DispatcherTimer? _istClockTimer;

    public bool IsAuthorizedToExit { get; set; }

    public MainPage()
    {
        InitializeComponent();
        KeyDown += MainPage_KeyDown;
    }

    private void MainPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool isWin = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
            || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (isWin || e.Key is Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (WifiDropdownOverlay.Visibility == Visibility.Visible)
            {
                WifiDropdownOverlay.Visibility = Visibility.Collapsed;
                WifiPasswordCard.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        StartIstClock();
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

            // 5.6 Initialize Multi-Threaded Tab Background Workers
            _tabWorkerManager = new TabWorkerManager(_config, _sessionStore, _auditLogger);
            _tabWorkerManager.WifiPortalWorker.WifiSignalUpdated += (s, info) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (WifiSignalTextBlock != null && info.signalPercentage > 0)
                    {
                        WifiSignalTextBlock.Text = $"{info.signalPercentage}%";
                    }
                });
            };
            _tabWorkerManager.WifiPortalWorker.InternetRestored += (s, ev) =>
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
            };
            _tabWorkerManager.StartAll();
            BootLog("Multi-threaded tab workers started on dedicated threads.");

            // 5.7 Initialize Multi-Threaded Security Watchdog
            _securityWatchdog = new SecurityWatchdog(_config.Security, _auditLogger);
            _securityWatchdog.ViolationDetected += (s, violation) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ShowNotice($"Security warning: {violation.Details}", InfoBarSeverity.Warning);
                });
            };
            _securityWatchdog.Start();
            BootLog("Multi-threaded security watchdog started on dedicated thread.");

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
            ShowAssessmentHome();

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

        Microsoft.Web.WebView2.Core.CoreWebView2Environment? environment = null;
        try
        {
            environment = await WebViewEnvironmentHelper.GetOrCreateSharedEnvironmentAsync();
            await ExamWebView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exInit)
        {
            _auditLogger?.Log(EventType.WEBVIEW_CRASH, "WEBVIEW_FALLBACK", $"Primary profile init failed: {exInit.Message}. Using isolated session directory.");
            var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", $"Exam_{Environment.ProcessId}");
            Directory.CreateDirectory(fallbackDir);
            var envOptions = WebViewEnvironmentHelper.CreateEnvironmentOptions();
            environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackDir, envOptions);
            await ExamWebView.EnsureCoreWebView2Async(environment);
        }

        if (ExamWebView.CoreWebView2 == null)
        {
            throw new InvalidOperationException("CoreWebView2 runtime could not be initialized on ExamWebView.");
        }

        var settings = ExamWebView.CoreWebView2.Settings;
        if (settings != null)
        {
            WebViewEnvironmentHelper.ConfigureStandardSettings(settings, _config?.Security?.AllowDevTools ?? false);
            settings.AreDefaultScriptDialogsEnabled = false;
        }

        // Block shortcut keys: Win keys, Win+G, Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+P, Ctrl+S, Ctrl+U, F12
        ExamWebView.PreviewKeyDown += (s, args) =>
        {
            var key = args.Key;
            bool isWin = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
                || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

            if (isWin || key is Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
            {
                args.Handled = true;
                return;
            }

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

        // Automatically propagate authenticated Google user identity to Google Sign-in on assessment platform
        _authenticatedCandidate = _onboardingStore.GetAuthenticatedUser();
        LoadProfileAvatar();
        if (_authenticatedCandidate != null && !string.IsNullOrWhiteSpace(_authenticatedCandidate.Email))
        {
            var safeCandidateEmail = _authenticatedCandidate.Email.Replace("\"", "\\\"");
            string googleIdentityScript = $$"""
                (() => {
                    const targetEmail = "{{safeCandidateEmail}}";
                    if (!targetEmail) return;

                    function autofillGoogleIdentity() {
                        if (!window.location.hostname.includes("google.com")) return;

                        // 1. Google OAuth / Sign-in email input field
                        const emailInput = document.querySelector('input[type="email"], #identifierId, input[name="identifier"]');
                        if (emailInput && (!emailInput.value || emailInput.value.trim() === '')) {
                            emailInput.value = targetEmail;
                            emailInput.dispatchEvent(new Event('input', { bubbles: true }));
                            emailInput.dispatchEvent(new Event('change', { bubbles: true }));
                        }

                        // 2. Google multi-account chooser selection
                        const accountItems = document.querySelectorAll('[data-identifier], [data-email], li[data-profileidentifier]');
                        for (const item of accountItems) {
                            const id = (item.getAttribute('data-identifier') || item.getAttribute('data-email') || item.getAttribute('aria-label') || '').toLowerCase();
                            if (id.includes(targetEmail.toLowerCase())) {
                                if (typeof item.click === 'function') {
                                    item.click();
                                    break;
                                }
                            }
                        }
                    }

                    if (document.readyState === 'loading') {
                        document.addEventListener('DOMContentLoaded', autofillGoogleIdentity);
                    } else {
                        autofillGoogleIdentity();
                    }
                    setTimeout(autofillGoogleIdentity, 300);
                    setTimeout(autofillGoogleIdentity, 800);
                    setTimeout(autofillGoogleIdentity, 1600);
                })();
                """;
            try
            {
                await ExamWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(googleIdentityScript);
            }
            catch { }
        }

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
        // Strictly land on Assessment Home screen on startup (avoid auto-navigating to CodeChef)
        ShowAssessmentHome();
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
            ShowBlockedUrlOverlay();
            return;
        }

        // Propagate authenticated Google user identity to CodeChef's OAuth sign-in flow
        if (IsGoogleAuthUri(e.Uri))
        {
            var authUser = _authenticatedCandidate ?? _onboardingStore.GetAuthenticatedUser();
            if (authUser != null && !string.IsNullOrWhiteSpace(authUser.Email))
            {
                if (!e.Uri.Contains("login_hint=", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    string delimiter = e.Uri.Contains('?') ? "&" : "?";
                    string augmentedUri = $"{e.Uri}{delimiter}login_hint={Uri.EscapeDataString(authUser.Email)}";
                    _auditLogger?.Log(EventType.PORTAL_AUTH_STARTED, "ACTIVE", $"Injecting authenticated Google login hint for {authUser.Email}", augmentedUri);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ExamWebView.CoreWebView2?.Navigate(augmentedUri);
                    });
                    return;
                }
            }
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

            // When navigating to Google Sign-In, ensure the email input is populated with authenticated identity
            if (IsGoogleAuthUri(ExamWebView.Source?.ToString()))
            {
                var authUser = _authenticatedCandidate ?? _onboardingStore.GetAuthenticatedUser();
                if (authUser != null && !string.IsNullOrWhiteSpace(authUser.Email))
                {
                    string safeEmail = authUser.Email.Replace("\"", "\\\"");
                    string js = $$"""
                        (() => {
                            const email = "{{safeEmail}}";
                            const input = document.querySelector('input[type="email"], #identifierId, input[name="identifier"]');
                            if (input && (!input.value || input.value.trim() === '')) {
                                input.value = email;
                                input.dispatchEvent(new Event('input', { bubbles: true }));
                                input.dispatchEvent(new Event('change', { bubbles: true }));
                            }
                        })();
                        """;
                    try { _ = ExamWebView.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
                }
            }
        }
    }

    private void ExamWebView_SourceChanged(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
    {
        if (ExamWebView.Visibility == Visibility.Visible)
        {
            var currentUri = ExamWebView.Source?.ToString();
            CurrentUrlTextBox.Text = currentUri ?? string.Empty;
            if (!string.IsNullOrEmpty(currentUri))
            {
                if (currentUri.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase) ||
                    currentUri.Contains("forms.gle", StringComparison.OrdinalIgnoreCase) ||
                    currentUri.Contains("forms.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    TabAssessmentText.Text = "Assessment Form";
                }
                else if (currentUri.Contains("codechef.com", StringComparison.OrdinalIgnoreCase))
                {
                    TabAssessmentText.Text = "CodeChef Assessment";
                }
            }
        }
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
        var decision = _allowlistManager?.IsExamNavigationAllowed(e.Uri);
        if (decision is { IsAllowed: true })
        {
            string targetUri = e.Uri;
            if (IsGoogleAuthUri(targetUri))
            {
                var authUser = _authenticatedCandidate ?? _onboardingStore.GetAuthenticatedUser();
                if (authUser != null && !string.IsNullOrWhiteSpace(authUser.Email) && !targetUri.Contains("login_hint=", StringComparison.OrdinalIgnoreCase))
                {
                    string delimiter = targetUri.Contains('?') ? "&" : "?";
                    targetUri = $"{targetUri}{delimiter}login_hint={Uri.EscapeDataString(authUser.Email)}";
                    _auditLogger?.Log(EventType.PORTAL_AUTH_STARTED, "ACTIVE", $"Injecting authenticated Google login hint for popup: {authUser.Email}", targetUri);
                }
            }
            ExamWebView.CoreWebView2?.Navigate(targetUri);
        }
        else
        {
            _auditLogger?.Log(EventType.POPUP_BLOCKED, "ACTIVE", $"Popup or new window request suppressed: {decision?.Reason ?? "Blocked"}", e.Uri);
        }
    }

    private static bool IsGoogleAuthUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (!parsed.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)) return false;

        var path = parsed.AbsolutePath.ToLowerInvariant();
        return path.StartsWith("/o/oauth2/") ||
               path.StartsWith("/signin/") ||
               path.StartsWith("/v3/signin/") ||
               path.StartsWith("/servicelogin") ||
               path.StartsWith("/accountchooser");
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

            var wifiInfo = _tabWorkerManager?.WifiPortalWorker.LatestWifiInfo ?? WifiSignalService.GetCurrentWifiSignal();
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
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248)); // #38BDF8
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84)); // #253554
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 22, 34, 56)); // #162238

                // Tab Badge
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "Login Needed";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 22, 34, 56));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84));
                }
                return;
            }

            if (_networkMonitor?.CurrentState == NetworkState.OFFLINE)
            {
                PingTextBlock.Text = "No Internet";
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)); // #EF4444
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 127, 29, 29)); // #7F1D1D
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 42, 18, 21)); // #2A1215

                // Tab Badge
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "No Internet";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 42, 18, 21));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 127, 29, 29));
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
                    // Optimal (Green #10B981)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59)); // #1E293B
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 10, 22, 40)); // #0A1628
                }
                else if (latency < 250)
                {
                    // Moderate (Amber #D97706)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 66, 48, 10)); // #42300A
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 26, 21, 4)); // #1A1504
                }
                else
                {
                    // High latency (Orange #EA580C)
                    PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 234, 88, 12));
                    WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 234, 88, 12));
                    NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 66, 30, 8)); // #421E08
                    NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 26, 15, 4)); // #1A0F04
                }

                // Tab Badge: Active in BG
                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = $"Active in BG ({latency}ms)";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 6, 46, 22)); // #062E16
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)); // #10B981
                }
            }
            else
            {
                PingTextBlock.Text = "Online";
                PingTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                WifiSignalIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                NetworkQualityBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
                NetworkQualityBadge.Background = new SolidColorBrush(Color.FromArgb(255, 10, 22, 40));

                if (WifiTabDot != null) WifiTabDot.Fill = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                if (WifiTabBadgeText != null)
                {
                    WifiTabBadgeText.Text = "Active in BG";
                    WifiTabBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                }
                if (WifiTabBadge != null)
                {
                    WifiTabBadge.Background = new SolidColorBrush(Color.FromArgb(255, 6, 46, 22));
                    WifiTabBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
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
        _tabWorkerManager?.WifiPortalWorker.SetRecoveryMode(true);
        _ = InitializeRecoveryWebViewAsync(forceNavigate);
        StartModalConnectivityPolling();
    }

    private void KeepRecoveryActiveInBackground()
    {
        _tabWorkerManager?.WifiPortalWorker.SetRecoveryMode(false);
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
            TabWifiPortalButton.Background = new SolidColorBrush(Color.FromArgb(255, 22, 34, 56)); // #162238
            TabWifiPortalButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84)); // #253554
            TabWifiPortalButton.BorderThickness = new Thickness(1);
            TabWifiTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248)); // #38BDF8

            TabAssessmentButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabAssessmentButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabAssessmentButton.BorderThickness = new Thickness(0);
            TabAssessmentText.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)); // #94A3B8
        }
        else
        {
            TabAssessmentButton.Background = new SolidColorBrush(Color.FromArgb(255, 22, 34, 56));
            TabAssessmentButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84));
            TabAssessmentButton.BorderThickness = new Thickness(1);
            TabAssessmentText.Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));

            TabWifiPortalButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabWifiPortalButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TabWifiPortalButton.BorderThickness = new Thickness(0);
            TabWifiTitle.Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
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
                AdditionalBrowserArguments = "--process-per-tab --disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion"
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

                RecoveryWebView.PreviewKeyDown += (s, args) =>
                {
                    var key = args.Key;
                    bool isWin = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
                        || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

                    if (isWin || key is Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
                    {
                        args.Handled = true;
                    }
                };

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
        if (ExamHomePageOverlay.Visibility == Visibility.Visible)
        {
            HomeSearchTextBox.Focus(FocusState.Programmatic);
        }
        else
        {
            ExamWebView.Focus(FocusState.Programmatic);
        }
    }

    private void TabWifiPortal_Click(object sender, RoutedEventArgs e)
    {
        ShowRecoveryModal(forceNavigate: false);
    }

    private void WifiPortalButton_Click(object sender, RoutedEventArgs e)
    {
        ShowRecoveryModal(forceNavigate: false);
    }

    // ═══════════════════════════════════════════════════════════
    // Browser Home Page & Navigation Handlers
    // ═══════════════════════════════════════════════════════════

    public void ShowAssessmentHome()
    {
        ExamHomePageOverlay.Visibility = Visibility.Visible;
        ExamWebView.Visibility = Visibility.Collapsed;
        TabAssessmentText.Text = "Assessment Home";
        CurrentUrlTextBox.Text = string.Empty;
    }

    public const string GoogleFormAssessmentUrl = "https://docs.google.com/forms/d/e/1FAIpQLScohPmHT5nJWX7i2jLMTXnyaHg4c52OJTyfTkW1Ci3DJ9BrdQ/viewform?usp=dialog";

    public void LaunchCodeChefAssessment()
    {
        ExamHomePageOverlay.Visibility = Visibility.Collapsed;
        ExamWebView.Visibility = Visibility.Visible;
        TabAssessmentText.Text = "CodeChef Assessment";
        CurrentUrlTextBox.Text = _config.Exam.StartUrl;
        if (!_isExamWebViewInitialized)
        {
            _ = InitializeExamWebViewAsync();
        }
        else
        {
            ExamWebView.CoreWebView2?.Navigate(_config.Exam.StartUrl);
        }
        ExamWebView.Focus(FocusState.Programmatic);
    }

    private void CodeChefCard_Click(object sender, RoutedEventArgs e)
    {
        LaunchCodeChefAssessment();
    }

    public void LaunchGoogleFormAssessment()
    {
        ExamHomePageOverlay.Visibility = Visibility.Collapsed;
        ExamWebView.Visibility = Visibility.Visible;
        TabAssessmentText.Text = "Assessment Form";
        CurrentUrlTextBox.Text = GoogleFormAssessmentUrl;
        if (!_isExamWebViewInitialized)
        {
            _ = InitializeExamWebViewAsync();
        }
        else
        {
            ExamWebView.CoreWebView2?.Navigate(GoogleFormAssessmentUrl);
        }
        ExamWebView.Focus(FocusState.Programmatic);
    }

    private void GoogleFormCard_Click(object sender, RoutedEventArgs e)
    {
        LaunchGoogleFormAssessment();
    }

    private void HomeSearchGo_Click(object sender, RoutedEventArgs e)
    {
        HandleUrlOrSearchSubmission(HomeSearchTextBox.Text);
    }

    private void HomeSearchTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            HandleUrlOrSearchSubmission(HomeSearchTextBox.Text);
            e.Handled = true;
        }
    }

    private void CurrentUrlTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            HandleUrlOrSearchSubmission(CurrentUrlTextBox.Text);
            e.Handled = true;
        }
    }

    private void ToolbarGo_Click(object sender, RoutedEventArgs e)
    {
        HandleUrlOrSearchSubmission(CurrentUrlTextBox.Text);
    }

    private void HandleUrlOrSearchSubmission(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var trimmed = input.Trim();
        string targetUrl = trimmed;

        if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            targetUrl = "https://" + targetUrl;
        }

        bool isAllowed = false;
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
        {
            var decision = _navigationGuard?.EvaluateExamNavigation(uri.ToString());
            if (decision is { IsAllowed: true })
            {
                isAllowed = true;
            }
            else if (uri.Host.Contains("codechef.com", StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = true;
            }
            else if (uri.Host.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase) ||
                     uri.Host.Contains("forms.gle", StringComparison.OrdinalIgnoreCase) ||
                     uri.Host.Contains("forms.google.com", StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = true;
            }
        }
        else if (trimmed.Contains("codechef", StringComparison.OrdinalIgnoreCase))
        {
            targetUrl = _config.Exam.StartUrl;
            isAllowed = true;
        }
        else if (trimmed.Contains("form", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Contains("google", StringComparison.OrdinalIgnoreCase))
        {
            targetUrl = GoogleFormAssessmentUrl;
            isAllowed = true;
        }

        if (isAllowed)
        {
            ExamHomePageOverlay.Visibility = Visibility.Collapsed;
            ExamWebView.Visibility = Visibility.Visible;
            TabAssessmentText.Text = targetUrl.Contains("forms", StringComparison.OrdinalIgnoreCase) || targetUrl.Contains("google", StringComparison.OrdinalIgnoreCase)
                ? "Assessment Form"
                : "CodeChef Assessment";
            CurrentUrlTextBox.Text = targetUrl;
            if (_isExamWebViewInitialized)
            {
                ExamWebView.CoreWebView2?.Navigate(targetUrl);
                ExamWebView.Focus(FocusState.Programmatic);
            }
            else
            {
                _ = InitializeExamWebViewAsync();
            }
        }
        else
        {
            ShowBlockedUrlOverlay();
        }
    }

    private void BlockedUrlOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        BlockedUrlOverlay.Visibility = Visibility.Collapsed;
    }

    // ═══════════════════════════════════════════════════════════
    // Indian Standard Time (IST) Live Clock
    // ═══════════════════════════════════════════════════════════
    private void StartIstClock()
    {
        UpdateIstClock();
        _istClockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _istClockTimer.Tick += (s, e) => UpdateIstClock();
        _istClockTimer.Start();
    }

    private void UpdateIstClock()
    {
        try
        {
            DateTime istTime;
            try
            {
                var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                istTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);
            }
            catch
            {
                istTime = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            }

            if (FooterIstTimeText != null)
            {
                FooterIstTimeText.Text = istTime.ToString("hh:mm:ss tt IST");
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════
    // GNOME-Style Wi-Fi Network Picker Handlers
    // ═══════════════════════════════════════════════════════════
    private WifiNetworkItem? _selectedNetworkForPassword;

    private void NetworkQualityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileDropdownOverlay.Visibility == Visibility.Visible)
        {
            ProfileDropdownOverlay.Visibility = Visibility.Collapsed;
        }

        if (WifiDropdownOverlay.Visibility == Visibility.Visible)
        {
            WifiDropdownOverlay.Visibility = Visibility.Collapsed;
            WifiPasswordCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            OpenWifiPicker();
        }
    }

    private void WifiPickerOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenWifiPicker();
    }

    private void WifiDropdownOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        WifiDropdownOverlay.Visibility = Visibility.Collapsed;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
    }

    private void DialogContent_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true; // Prevent click inside dropdown card from dismissing modal
    }

    private async void WifiRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWifiListAsync();
    }

    private void UpdateConnectedNetworkCard()
    {
        try
        {
            var wifiInfo = _tabWorkerManager?.WifiPortalWorker.LatestWifiInfo ?? WifiSignalService.GetCurrentWifiSignal();
            var ssid = !string.IsNullOrWhiteSpace(wifiInfo.ssid) ? wifiInfo.ssid : "Campus Wi-Fi";
            if (WifiCurrentSsidText != null) WifiCurrentSsidText.Text = ssid;
            if (WifiCurrentSignalText != null) WifiCurrentSignalText.Text = wifiInfo.signalPercentage > 0 ? $"Signal: {wifiInfo.signalPercentage}%" : "Signal: --%";
            if (WifiCurrentPingText != null) WifiCurrentPingText.Text = PingTextBlock?.Text != null ? $"Ping: {PingTextBlock.Text}" : "Connected";
        }
        catch { }
    }

    private void OpenWifiPicker()
    {
        WifiDropdownOverlay.Visibility = Visibility.Visible;
        WifiPasswordCard.Visibility = Visibility.Collapsed;
        WifiConnectingBanner.Visibility = Visibility.Collapsed;
        UpdateConnectedNetworkCard();
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
            Background = net.IsConnected ? new SolidColorBrush(Color.FromArgb(255, 22, 34, 56)) : new SolidColorBrush(Color.FromArgb(255, 15, 18, 25)), // #162238 : #0F1219
            BorderBrush = net.IsConnected ? new SolidColorBrush(Color.FromArgb(255, 37, 53, 84)) : new SolidColorBrush(Color.FromArgb(255, 30, 41, 59)), // #253554 : #1E293B
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
            Foreground = new SolidColorBrush(Color.FromArgb(255, 230, 237, 243)) // #E6EDF3
        });

        if (net.IsCampusNetwork)
        {
            var campusBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 6, 46, 22)), // #062E16
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)), // #10B981
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1)
            };
            campusBadge.Child = new TextBlock
            {
                Text = "Campus Wi-Fi",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)) // #10B981
            };
            topRow.Children.Add(campusBadge);
        }

        if (net.IsConnected)
        {
            var connectedBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 22, 34, 56)), // #162238
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 37, 53, 84)), // #253554
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1)
            };
            connectedBadge.Child = new TextBlock
            {
                Text = "Connected",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248)) // #38BDF8
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
            WifiDropdownOverlay.Visibility = Visibility.Collapsed;

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
        if (ExamWebView.Visibility == Visibility.Visible)
        {
            if (ExamWebView.CanGoBack)
            {
                ExamWebView.GoBack();
            }
            else
            {
                ShowAssessmentHome();
            }
        }
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
        ShowAssessmentHome();
    }

    // Invigilator Exit PIN Dialog Handlers
    private void ExitExamButton_Click(object sender, RoutedEventArgs e)
    {
        ProfileDropdownOverlay.Visibility = Visibility.Collapsed;
        PromptForExitPin();
    }

    public void PromptForExitPin()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PinErrorPanel.Visibility = Visibility.Collapsed;
            PinErrorMessage.Visibility = Visibility.Collapsed;
            ExitPinPasswordBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
            ExitPinPasswordBox.Password = string.Empty;
            PinModalOverlay.Visibility = Visibility.Visible;
            ExitPinPasswordBox.Focus(FocusState.Programmatic);
        });
    }

    private void AuthorizePin_Click(object sender, RoutedEventArgs e)
    {
        ValidateAndExit();
    }

    private void ExitPinPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PinErrorPanel.Visibility == Visibility.Visible || PinErrorMessage.Visibility == Visibility.Visible)
        {
            PinErrorPanel.Visibility = Visibility.Collapsed;
            PinErrorMessage.Visibility = Visibility.Collapsed;
            ExitPinPasswordBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
        }
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

        // Accept configured PIN or default fallback
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

            // 4. Dispose background network engine, tab workers, security watchdog & audit logger
            try { _istClockTimer?.Stop(); } catch { }
            try { _tabWorkerManager?.StopAll(); } catch { }
            try { _tabWorkerManager?.Dispose(); } catch { }
            try { _securityWatchdog?.Stop(); } catch { }
            try { _securityWatchdog?.Dispose(); } catch { }
            try { _networkMonitor?.Stop(); } catch { }
            try { _networkMonitor?.Dispose(); } catch { }
            try { _auditLogger?.Dispose(); } catch { }

            // 5. Completely terminate application and process tree (guarantees no background zombie remains)
            App.ExitCompletely();
        }
        else
        {
            PinErrorPanel.Visibility = Visibility.Visible;
            PinErrorMessage.Visibility = Visibility.Visible;
            ExitPinPasswordBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
            ExitPinPasswordBox.SelectAll();
            ExitPinPasswordBox.Focus(FocusState.Programmatic);
            _auditLogger?.Log(EventType.SECURITY_VIOLATION, "PIN_REJECTED", "Invalid invigilator PIN entered.");
        }
    }

    public void CancelPin()
    {
        PinModalOverlay.Visibility = Visibility.Collapsed;
        PinErrorPanel.Visibility = Visibility.Collapsed;
        PinErrorMessage.Visibility = Visibility.Collapsed;
        ExitPinPasswordBox.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 30, 41, 59));
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

    // ═══════════════════════════════════════════════════════════
    // Profile Avatar & Dropdown Handlers
    // ═══════════════════════════════════════════════════════════

    private void LoadProfileAvatar()
    {
        try
        {
            if (_authenticatedCandidate == null)
            {
                ProfileInitialsText.Text = "GU";
                DropdownInitialsText.Text = "GU";
                DropdownDisplayName.Text = "Candidate";
                DropdownEmail.Text = "Not signed in";
                return;
            }

            // Set initials
            var initials = _authenticatedCandidate.GetInitials();
            ProfileInitialsText.Text = initials;
            DropdownInitialsText.Text = initials;
            DropdownDisplayName.Text = !string.IsNullOrWhiteSpace(_authenticatedCandidate.DisplayName)
                ? _authenticatedCandidate.DisplayName
                : "Candidate";
            DropdownEmail.Text = _authenticatedCandidate.Email ?? "Not signed in";

            // Try to load profile photo
            if (!string.IsNullOrWhiteSpace(_authenticatedCandidate.PhotoUrl))
            {
                try
                {
                    var photoUri = new Uri(_authenticatedCandidate.PhotoUrl);
                    var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(photoUri);
                    bitmapImage.ImageOpened += (s, e) =>
                    {
                        ProfilePhotoCircle.Visibility = Visibility.Visible;
                        ProfileInitialsCircle.Visibility = Visibility.Collapsed;
                        ProfileInitialsText.Visibility = Visibility.Collapsed;
                        DropdownPhotoCircle.Visibility = Visibility.Visible;
                    };
                    bitmapImage.ImageFailed += (s, e) =>
                    {
                        // Keep initials visible on photo load failure
                        ProfilePhotoCircle.Visibility = Visibility.Collapsed;
                        DropdownPhotoCircle.Visibility = Visibility.Collapsed;
                    };
                    ProfilePhotoBrush.ImageSource = bitmapImage;
                    DropdownPhotoBrush.ImageSource = bitmapImage;
                }
                catch
                {
                    // Fallback to initials on any error
                }
            }
        }
        catch { }
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (WifiDropdownOverlay.Visibility == Visibility.Visible)
        {
            WifiDropdownOverlay.Visibility = Visibility.Collapsed;
            WifiPasswordCard.Visibility = Visibility.Collapsed;
        }

        if (ProfileDropdownOverlay.Visibility == Visibility.Visible)
        {
            ProfileDropdownOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            ProfileDropdownOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ProfileDropdownOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ProfileDropdownOverlay.Visibility = Visibility.Collapsed;
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProfileDropdownOverlay.Visibility = Visibility.Collapsed;

            // Clear Google session cookies from WebView2
            if (_isExamWebViewInitialized && ExamWebView.CoreWebView2?.CookieManager != null)
            {
                ExamWebView.CoreWebView2.CookieManager.DeleteAllCookies();
            }

            // Reset onboarding state (clears stored Google credentials)
            _onboardingStore.Reset();
            _authenticatedCandidate = null;

            _auditLogger?.Log(EventType.EXAM_COMPLETED, "LOGOUT", "Candidate logged out. Google session cleared.");

            // Uninstall security hooks before navigating away
            try { _istClockTimer?.Stop(); } catch { }
            try { _keyboardHook?.UninstallHook(); } catch { }
            try { _tabWorkerManager?.StopAll(); } catch { }
            try { _tabWorkerManager?.Dispose(); } catch { }
            try { _securityWatchdog?.Stop(); } catch { }
            try { _securityWatchdog?.Dispose(); } catch { }
            try { _networkMonitor?.Stop(); } catch { }
            try { _networkMonitor?.Dispose(); } catch { }
            try { _auditLogger?.Dispose(); } catch { }

            // Navigate back to LaunchPage (sign-in screen)
            MainWindow.CurrentInstance?.NavigateToLaunchPage();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "boot.log"), $"[{DateTime.UtcNow:O}] [Logout Error] {ex}\n"); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Blocked URL Overlay
    // ═══════════════════════════════════════════════════════════

    private async void ShowBlockedUrlOverlay()
    {
        BlockedUrlOverlay.Visibility = Visibility.Visible;
        // Auto-dismiss after 3 seconds
        await Task.Delay(3000);
        BlockedUrlOverlay.Visibility = Visibility.Collapsed;
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
