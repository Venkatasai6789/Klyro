using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Klyro_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static App? _instance;
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        _instance = this;
        InitializeComponent();

        // Prevent WinUI from calling RoFailFastWithErrorContext on internal XAML errors
        // This is the primary defense against 0xc000027b (STATUS_STOWED_EXCEPTION) crashes
        try { DebugSettings.FailFastOnErrors = false; } catch { }

        UnhandledException += (sender, e) =>
        {
            try
            {
                var baseDir = System.AppContext.BaseDirectory;
                var crashFile = System.IO.Path.Combine(baseDir, "crash.log");
                var bootLog = System.IO.Path.Combine(baseDir, "boot.log");
                var msg = $"[{DateTime.UtcNow:O}] [WinUI UnhandledException] Message: {e.Message}\nException: {e.Exception}\nStackTrace: {e.Exception?.StackTrace}\n";
                System.IO.File.AppendAllText(crashFile, msg);
                System.IO.File.AppendAllText(bootLog, msg);
            }
            catch { }

            // Mark handled to prevent WinRT fail-fast stowed exception (0xc000027b)
            e.Handled = true;
        };
    }

    /// <summary>
    /// Brings the active MainWindow to the foreground when a secondary launch is detected.
    /// </summary>
    public static void BringToFront()
    {
        var win = _instance?._window;
        if (win == null) return;

        win.DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                win.Activate();
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
                if (hWnd != IntPtr.Zero)
                {
                    Program.RestoreAndActivateWindow(hWnd);
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        string bootLog = System.IO.Path.Combine(System.AppContext.BaseDirectory, "boot.log");
        try
        {
            System.IO.File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] OnLaunched: Creating MainWindow...\n");
            _window = new MainWindow();
            System.IO.File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] OnLaunched: Activating MainWindow...\n");
            _window.Activate();
            System.IO.File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] OnLaunched: MainWindow activated successfully.\n");
        }
        catch (Exception ex)
        {
            var crashFile = System.IO.Path.Combine(System.AppContext.BaseDirectory, "crash.log");
            System.IO.File.AppendAllText(crashFile, $"[{DateTime.UtcNow:O}] [OnLaunched Exception] {ex}\n");
            System.IO.File.AppendAllText(bootLog, $"[{DateTime.UtcNow:O}] [OnLaunched Exception] {ex}\n");
            throw;
        }
    }
}
