using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Klyro_App;

public static class Program
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [System.STAThread]
    public static void Main(string[] args)
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "boot.log");
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [AppDomain Unhandled] {e.ExceptionObject}\n"); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [ProcessExit] StackTrace:\n{Environment.StackTrace}\n"); } catch { }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [UnobservedTaskException] {e.Exception}\n"); } catch { }
            };

            // Enforce single instance to prevent WebView2 profile folder contention
            const string mutexName = "Global\\Klyro_Secure_Assessment_Browser_SingleInstance";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);
            if (!isNewInstance)
            {
                try
                {
                    var current = Process.GetCurrentProcess();
                    var existing = Process.GetProcessesByName(current.ProcessName)
                        .FirstOrDefault(p => p.Id != current.Id);

                    if (existing != null && existing.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(existing.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(existing.MainWindowHandle);
                    }
                }
                catch { }

                return;
            }

            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 1. Main entered. Args: {string.Join(" ", args)}\n");
            
            WinRT.ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 2. ComWrappers initialized.\n");

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                try
                {
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 3. Inside Application.Start\n");
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 4. App instantiated successfully.\n");
                }
                catch (Exception exStart)
                {
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [Application.Start callback Exception] {exStart}\n");
                }
            });

            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 5. Application.Start returned normally.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [Fatal Main Exception] {ex}\n");
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
        }
    }
}
