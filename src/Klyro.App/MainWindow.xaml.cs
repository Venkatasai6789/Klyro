using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Klyro.Core.Persistence;
using Klyro.Core.Security;

namespace Klyro_App;

/// <summary>
/// The application window hosting Klyro in locked full-screen or startup presentation mode.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_MINIMIZE = 6;

    public static MainWindow? CurrentInstance { get; private set; }

    public event EventHandler<WindowActivatedEventArgs>? WindowActivated;

    private readonly OnboardingStateStore _onboardingStore = new();
    private readonly KeyboardHookManager _keyboardHook = new();
    private bool _isFirstRun;
    private bool _isFullScreenConfigured;

    public MainWindow()
    {
        CurrentInstance = this;
        InitializeComponent();
        Title = Program.WindowTitle;

        // Install global low-level keyboard hook immediately on application boot
        _keyboardHook.InstallHook();

        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd != IntPtr.Zero)
            {
                SetWindowText(hWnd, Program.WindowTitle);
            }
        }
        catch { }

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch { }

        // Check if this is the first-run onboarding experience (supports --first-run, --onboarding, --screen1, --screen2, --welcome, --screen3 CLI flags)
        bool forceFirstRun = Environment.GetCommandLineArgs().Any(a => 
            a.Equals("--first-run", StringComparison.OrdinalIgnoreCase) || 
            a.Equals("--onboarding", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen1", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen2", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--welcome", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen3", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--account-setup", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen4", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--account-connected", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen5", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--preflight-prepare", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--screen6", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--preflight-checking", StringComparison.OrdinalIgnoreCase));
        _isFirstRun = forceFirstRun || !_onboardingStore.IsFirstRunCompleted();

        // Enforce mode upon window activation
        Activated += MainWindow_Activated;

        // Intercept closing attempts to require Invigilator PIN authorization during active exams
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Destroying += (s, e) => { _keyboardHook.UninstallHook(); App.ExitCompletely(); };
        Closed += (s, e) => { _keyboardHook.UninstallHook(); App.ExitCompletely(); };

        // Always boot through LaunchPage (Screen 1 Startup & Diagnostic Pipeline) for both new and existing users.
        // Returning candidates execute preflight verification before entering the exam view.
        try
        {
            ConfigureStartupWindowLayout();
            RootFrame.Navigate(typeof(LaunchPage));
        }
        catch (Exception ex)
        {
            string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
            try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [MainWindow Navigation Exception] {ex}\n"); } catch { }
        }
    }

    private void ConfigureStartupWindowLayout()
    {
        string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
        try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [MainWindow] ConfigureStartupWindowLayout starting...\n"); } catch { }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                op.IsResizable = true;
                op.IsMinimizable = false;
                op.IsMaximizable = false;
                op.SetBorderAndTitleBar(true, false); // Border enabled, system titlebar disabled — no window chrome controls
                try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [MainWindow] OverlappedPresenter configured.\n"); } catch { }
            }

            // Size window to 1536 x 1024 or optimal display proportion
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                int targetWidth = Math.Min(1536, (int)(displayArea.WorkArea.Width * 0.95));
                int targetHeight = Math.Min(1024, (int)(displayArea.WorkArea.Height * 0.95));

                int posX = Math.Max(0, (displayArea.WorkArea.Width - targetWidth) / 2);
                int posY = Math.Max(0, (displayArea.WorkArea.Height - targetHeight) / 2);

                AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
                AppWindow.Move(new PointInt32(posX, posY));
                try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [MainWindow] Window resized to {targetWidth}x{targetHeight} at ({posX},{posY}).\n"); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [MainWindow ConfigureStartupWindowLayout Exception] {ex}\n"); } catch { }
        }
    }

    public void MinimizeWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                op.Minimize();
            }
            else
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hWnd, SW_MINIMIZE);
            }
        }
        catch { }
    }

    public bool ToggleMaximizeRestoreWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                if (op.State == OverlappedPresenterState.Maximized)
                {
                    op.Restore();
                    return false;
                }
                else
                {
                    op.Maximize();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public void NavigateToAssessmentView()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                RootFrame.Navigate(typeof(MainPage));
                _isFullScreenConfigured = false;
                ConfigureFullScreenExamMode();
            }
            catch (Exception ex)
            {
                string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
                try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [NavigateToAssessmentView Exception] {ex}\n"); } catch { }
            }
        });
    }

    public void NavigateToLaunchPage()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                RootFrame.Navigate(typeof(LaunchPage));
            }
            catch (Exception ex)
            {
                string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
                try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [NavigateToLaunchPage Exception] {ex}\n"); } catch { }
            }
        });
    }

    private void ConfigureFullScreenExamMode()
    {
        try
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch
        {
            try
            {
                var presenter = OverlappedPresenter.CreateForToolWindow();
                presenter.Maximize();
                AppWindow.SetPresenter(presenter);
            }
            catch { }
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        try
        {
            WindowActivated?.Invoke(this, args);
        }
        catch { }

        if (RootFrame.Content is MainPage && !_isFullScreenConfigured && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isFullScreenConfigured = true;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ConfigureFullScreenExamMode();
            });
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        string bootLog = Path.Combine(AppContext.BaseDirectory, "boot.log");
        try { File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] AppWindow_Closing triggered. Content: {RootFrame.Content?.GetType().Name}\n"); } catch { }

        if (RootFrame.Content is MainPage mainPage)
        {
            if (!mainPage.IsAuthorizedToExit)
            {
                args.Cancel = true;
                mainPage.PromptForExitPin();
                return;
            }
        }
        else
        {
            // On startup/onboarding screens, clean exit is permitted without PIN lock
            App.ExitCompletely();
            return;
        }

        // When authorized to exit, ensure entire process terminates cleanly
        App.ExitCompletely();
    }
}
