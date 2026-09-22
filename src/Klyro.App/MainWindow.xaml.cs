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

        // Enforce true full-screen mode to cover the entire display and hide the Windows taskbar
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);

        // Intercept closing attempts to require Invigilator PIN authorization
        AppWindow.Closing += AppWindow_Closing;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (RootFrame.Content is MainPage mainPage && !mainPage.IsAuthorizedToExit)
        {
            args.Cancel = true;
            mainPage.PromptForExitPin();
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
