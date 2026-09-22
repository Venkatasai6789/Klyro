using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Klyro.Core.Persistence;
using Klyro.Core.Startup;

namespace Klyro_App;

public sealed partial class LaunchPage : Page
{
    private const double MaxProgressBarWidth = 575.0;
    private readonly StartupPipeline _pipeline = new();
    private readonly OnboardingStateStore _onboardingStore = new();
    private CancellationTokenSource? _startupCts;
    private double _currentProgressWidth;

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

            PageLog("Calling _pipeline.RunAsync...");
            var result = await _pipeline.RunAsync(progressHandler, ct);
            PageLog($"_pipeline.RunAsync completed. Result: {result.IsSuccess}, Error: {result.ErrorMessage}");

            if (result.IsSuccess)
            {
                UpdateProgress(100, "Ready.");
                await Task.Delay(400);

                // Mark first run completed in persistent store
                try
                {
                    PageLog("Marking onboarding as completed...");
                    _onboardingStore.MarkCompleted();
                }
                catch (Exception exStore)
                {
                    PageLog($"OnboardingStore error: {exStore}");
                }

                // Transition to the exam workspace in full-screen mode
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
}
