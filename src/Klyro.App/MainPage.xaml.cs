using System;
using System.IO;
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

    private SessionStore? _sessionStore;
    private NetworkMonitor? _networkMonitor;
    private RecoveryController? _recoveryController;
    private ExamStateMachine? _examStateMachine;

    private bool _isExamWebViewInitialized;
    private bool _isRecoveryWebViewInitialized;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeSystemAsync();
    }

    private async Task InitializeSystemAsync()
    {
        try
        {
            // 1. Load Configuration
            _config = LoadConfiguration();

            // 2. Initialize Audit Logger
            _auditLogger = new AuditLogger();
            _auditLogger.Log(EventType.EXAM_STARTED, "INITIALIZING", "Klyro shell launched.");

            // 3. Initialize Security Guards
            _allowlistManager = new AllowlistManager(_config);
            _navigationGuard = new NavigationGuard(_allowlistManager);
            _frameGuard = new FrameNavigationGuard(_allowlistManager);
            _popupGuard = new PopupGuard(_allowlistManager, _config.Security);
            _downloadGuard = new DownloadGuard(_allowlistManager, _config.Security);
            _protocolGuard = new ProtocolGuard();

            // 4. Initialize Persistence & Recovery
            _sessionStore = new SessionStore();
            var existingSession = _sessionStore.LoadActiveSession();

            // 5. Initialize Network Engine
            _networkMonitor = new NetworkMonitor(_config, auditLogger: _auditLogger);
            _recoveryController = new RecoveryController(_networkMonitor, _config.Network, _auditLogger);

            _networkMonitor.StateChanged += OnNetworkStateChanged;
            _recoveryController.StateChanged += OnRecoveryStateChanged;
            _recoveryController.ExamResumeRequested += OnExamResumeRequested;

            _networkMonitor.Start();

            // 6. Initialize Exam State Machine & Native Timer
            _examStateMachine = new ExamStateMachine(_config, _sessionStore, _auditLogger, existingSession);
            _examStateMachine.Timer.Tick += OnTimerTick;
            _examStateMachine.Timer.TimeExpired += OnTimerExpired;

            // Show dev simulation toggle if configured
            if (_config.Network.EnableNetworkSimulator || string.Equals(_config.Application.Environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                SimulatorToggleButton.Visibility = Visibility.Visible;
            }

            // 7. Execute Pre-flight Checks
            await RunPreflightValidationAsync();
        }
        catch (Exception ex)
        {
            ShowPreflightError($"Fatal startup error: {ex.Message}");
        }
    }

    private async Task RunPreflightValidationAsync()
    {
        PreflightProgressBar.IsIndeterminate = true;
        PreflightErrorMessage.Visibility = Visibility.Collapsed;
        PreflightRetryButton.Visibility = Visibility.Collapsed;

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

            await Task.Delay(800); // brief institutional confirmation
            PreflightOverlay.Visibility = Visibility.Collapsed;

            await InitializeExamWebViewAsync();
        }
        else
        {
            ShowPreflightError("One or more critical pre-flight security checks failed. Please contact the invigilator.");
        }
    }

    private void ShowPreflightError(string message)
    {
        PreflightProgressBar.IsIndeterminate = false;
        PreflightErrorMessage.Text = message;
        PreflightErrorMessage.Visibility = Visibility.Visible;
        PreflightRetryButton.Visibility = Visibility.Visible;
    }

    private async void PreflightRetry_Click(object sender, RoutedEventArgs e)
    {
        await RunPreflightValidationAsync();
    }

    private async Task InitializeExamWebViewAsync()
    {
        if (_isExamWebViewInitialized) return;

        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", "Exam");
        var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, profileDir, null);

        await ExamWebView.EnsureCoreWebView2Async(environment);

        var settings = ExamWebView.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = _config.Security.AllowDevTools;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;

        // Attach strict security guards
        ExamWebView.CoreWebView2.NavigationStarting += ExamWebView_NavigationStarting;
        ExamWebView.CoreWebView2.FrameNavigationStarting += ExamWebView_FrameNavigationStarting;
        ExamWebView.CoreWebView2.NewWindowRequested += ExamWebView_NewWindowRequested;
        ExamWebView.CoreWebView2.DownloadStarting += ExamWebView_DownloadStarting;

        _isExamWebViewInitialized = true;

        var targetUrl = _examStateMachine?.CurrentSession.LastKnownAssessmentUrl ?? _config.Exam.StartUrl;
        ExamWebView.CoreWebView2.Navigate(targetUrl);

        _examStateMachine?.TransitionTo(ExamState.ACTIVE);
    }

    private void ExamWebView_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        var decision = _navigationGuard?.EvaluateExamNavigation(e.Uri);
        if (decision is { IsAllowed: false })
        {
            e.Cancel = true;
            _auditLogger?.Log(EventType.NAVIGATION_BLOCKED, "ACTIVE", decision.Reason, e.Uri);
            ShowNotice($"Navigation blocked: {decision.Reason}", InfoBarSeverity.Error);
        }
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
        e.Handled = true; // Block popups and new tabs unconditionally
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
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (newState)
            {
                case NetworkState.ONLINE:
                    NetworkStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 22, 163, 74));
                    NetworkStatusTextBlock.Text = "Connected";
                    NoticeBar.IsOpen = false;
                    break;

                case NetworkState.DEGRADED:
                    NetworkStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    NetworkStatusTextBlock.Text = "Connection Unstable";
                    break;

                case NetworkState.PORTAL_REQUIRED:
                    NetworkStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
                    NetworkStatusTextBlock.Text = "Authentication Required";
                    break;

                case NetworkState.OFFLINE:
                    NetworkStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 220, 38, 38));
                    NetworkStatusTextBlock.Text = "Offline";
                    ShowNotice("Campus Wi-Fi connection lost. Searching for signal...", InfoBarSeverity.Warning);
                    break;

                case NetworkState.ASSESSMENT_SERVICE_UNAVAILABLE:
                    NetworkStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6));
                    NetworkStatusTextBlock.Text = "Service Unavailable";
                    ShowNotice("Assessment service temporarily unavailable. Your session is preserved. Do not reload.", InfoBarSeverity.Warning);
                    break;
            }
        });
    }

    private void OnRecoveryStateChanged(RecoveryState previousState, RecoveryState newState)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            switch (newState)
            {
                case RecoveryState.LoginRequired:
                    RecoveryModalOverlay.Visibility = Visibility.Visible;
                    RecoveryStatusPanel.Visibility = Visibility.Collapsed;
                    RecoveryRetryButton.Visibility = Visibility.Collapsed;
                    await InitializeRecoveryWebViewAsync();
                    break;

                case RecoveryState.Authenticating:
                    RecoveryStatusPanel.Visibility = Visibility.Visible;
                    RecoveryStatusText.Text = "Authenticating... verifying Internet connection";
                    break;

                case RecoveryState.ConnectionRestored:
                    RecoveryStatusPanel.Visibility = Visibility.Visible;
                    RecoveryStatusText.Text = "Connection Restored! Returning to assessment...";
                    await Task.Delay(1000);
                    RecoveryModalOverlay.Visibility = Visibility.Collapsed;
                    break;

                case RecoveryState.RecoveryFailed:
                    RecoveryStatusPanel.Visibility = Visibility.Visible;
                    RecoveryStatusText.Text = "Network connection could not be restored.";
                    RecoveryRetryButton.Visibility = Visibility.Visible;
                    break;

                case RecoveryState.Hidden:
                    RecoveryModalOverlay.Visibility = Visibility.Collapsed;
                    break;
            }
        });
    }

    private async Task InitializeRecoveryWebViewAsync()
    {
        if (!_isRecoveryWebViewInitialized)
        {
            var recoveryProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro", "Profiles", "Recovery");
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, recoveryProfileDir, null);

            await RecoveryWebView.EnsureCoreWebView2Async(environment);

            var settings = RecoveryWebView.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;

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

            // Detect portal login form submission
            RecoveryWebView.CoreWebView2.WebMessageReceived += async (s, e) =>
            {
                await _recoveryController!.HandleCredentialsSubmittedAsync();
            };

            _isRecoveryWebViewInitialized = true;
        }

        RecoveryWebView.CoreWebView2.Navigate(_config.Network.PortalUrl);
    }

    private void OnExamResumeRequested(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ExamWebView.Focus(FocusState.Programmatic);
            _examStateMachine?.TransitionTo(ExamState.ACTIVE);
            ShowNotice("Connection verified. Examination resumed.", InfoBarSeverity.Success);
        });
    }

    private async void RecoveryRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_recoveryController != null)
        {
            await _recoveryController.HandleCredentialsSubmittedAsync();
        }
    }

    private void OnTimerTick(TimeSpan remaining, TimeSpan elapsed)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimerTextBlock.Text = $"{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        });
    }

    private void OnTimerExpired(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimerTextBlock.Text = "00:00:00";
            ShowNotice("Examination time has expired. Please submit your assessment.", InfoBarSeverity.Informational);
        });
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
        string[] candidates = { "config/exam.json", "config/development.json", "config/default.json" };
        foreach (var file in candidates)
        {
            if (File.Exists(file))
            {
                return ConfigurationLoader.LoadFromFile(file);
            }
        }
        return new KlyroConfiguration();
    }
}
