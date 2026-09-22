using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Klyro_App;

/// <summary>
/// The application window hosting Klyro in locked full-screen presentation mode.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    public MainWindow()
    {
        InitializeComponent();
        Title = Program.WindowTitle;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hWnd != IntPtr.Zero)
        {
            SetWindowText(hWnd, Program.WindowTitle);
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // Enforce true full-screen mode upon window activation to cover entire display and hide taskbar
        Activated += MainWindow_Activated;

        // Intercept closing attempts to require Invigilator PIN authorization
        AppWindow.Closing += AppWindow_Closing;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private bool _isFullScreenConfigured;

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_isFullScreenConfigured && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isFullScreenConfigured = true;
            try
            {
                AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            }
            catch
            {
                // Fallback: maximize with hidden title bar if FullScreen presenter fails (e.g., remote desktop sessions)
                try
                {
                    var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForToolWindow();
                    presenter.Maximize();
                    AppWindow.SetPresenter(presenter);
                }
                catch { }
            }
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
            }
        }
        else
        {
            args.Cancel = true;
        }
    }
}
