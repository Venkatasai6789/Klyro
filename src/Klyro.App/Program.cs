using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Klyro_App;

public static class Program
{
    public const string WindowTitle = "Klyro Secure Assessment Browser";
    private const string MutexName = @"Local\Klyro_Secure_Assessment_Browser_SingleInstance";
    private const string ActivateEventName = @"Local\Klyro_Secure_Assessment_Browser_ActivateEvent";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static volatile bool _isExiting;

    // WER: Disable Windows Error Reporting crash dialogs to prevent 0xc000027b terminations
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void RestoreAndActivateWindow(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            try
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            catch { }
        }
    }

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "boot.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch { }
    }

    [System.STAThread]
    public static void Main(string[] args)
    {
        // Suppress WER crash dialogs and native GPF error boxes at the OS level
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

        Log($"Main entered. PID: {Environment.ProcessId}. Args: {string.Join(" ", args)}");

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log($"[AppDomain Unhandled] {e.ExceptionObject}");
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                _isExiting = true;
                Log($"[ProcessExit] PID: {Environment.ProcessId}");
            };
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                // Log critical first-chance exceptions that could cause WinUI stowed exception crashes.
                // Filter to reduce noise — only log exceptions from WinUI / XAML / COM internals.
                var ex = e.Exception;
                if (ex is System.Runtime.InteropServices.COMException ||
                    ex is InvalidOperationException ||
                    ex is AccessViolationException ||
                    ex is NullReferenceException)
                {
                    Log($"[FirstChance] {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log($"[UnobservedTaskException] {e.Exception}");
                e.SetObserved();
            };

            // 1. Single-Instance Check via Mutex & EventWaitHandle
            bool isNewInstance = false;
            try
            {
                _singleInstanceMutex = new Mutex(true, MutexName, out isNewInstance);
            }
            catch (AbandonedMutexException)
            {
                Log("Acquired abandoned single-instance mutex. Proceeding as primary instance.");
                isNewInstance = true;
            }
            catch (Exception exMutex)
            {
                Log($"Mutex check encountered exception: {exMutex.Message}. Proceeding as new instance.");
                isNewInstance = true;
            }

            if (!isNewInstance)
            {
                var current = Process.GetCurrentProcess();
                var others = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id)
                    .ToList();

                if (others.Count > 0)
                {
                    Log($"Secondary instance detected running primary process (PID {others[0].Id}). Signaling activation event...");
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existingEvent))
                        {
                            existingEvent.Set();
                            existingEvent.Dispose();
                            Log("Primary instance signaled to activate window. Exiting secondary instance cleanly.");
                        }
                        else
                        {
                            Log("Could not open activation event. Exiting secondary instance.");
                        }
                    }
                    catch (Exception exSignal)
                    {
                        Log($"Activation signaling failed: {exSignal.Message}. Exiting secondary instance.");
                    }

                    return;
                }
                else
                {
                    Log("Mutex was held but no other active process was found. Continuing as primary instance.");
                }
            }

            // 2. Initialize Activation Listener Event
            try
            {
                _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                var listener = new Thread(() =>
                {
                    while (!_isExiting)
                    {
                        try
                        {
                            if (_activationEvent.WaitOne(1000))
                            {
                                if (_isExiting) break;
                                Log("Activation signal received from secondary instance. Bringing window to front.");
                                App.BringToFront();
                            }
                        }
                        catch { }
                    }
                })
                {
                    IsBackground = true,
                    Name = "Klyro_SingleInstance_Listener"
                };
                listener.Start();
            }
            catch (Exception exEvent)
            {
                Log($"Failed to register activation event listener: {exEvent.Message}");
            }

            // Production heartbeat: logs every 60s for health monitoring and crash post-mortem
            var heartbeat = new Thread(() =>
            {
                int tick = 0;
                while (!_isExiting)
                {
                    try { Thread.Sleep(60_000); } catch { break; }
                    tick++;
                    var mem = GC.GetTotalMemory(false) / (1024 * 1024);
                    Log($"[Heartbeat] #{tick} PID:{Environment.ProcessId} ManagedMB:{mem} Threads:{Process.GetCurrentProcess().Threads.Count}");
                }
            })
            {
                IsBackground = true,
                Name = "Klyro_Heartbeat"
            };
            heartbeat.Start();

            Log("Initializing WinRT ComWrappers...");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("ComWrappers initialized successfully.");

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                try
                {
                    Log("Inside Application.Start: setting synchronization context...");
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    Log("App instantiated successfully.");
                }
                catch (Exception exStart)
                {
                    Log($"[Application.Start callback Exception] {exStart}");
                }
            });

            Log("Application.Start returned normally.");
        }
        catch (Exception ex)
        {
            Log($"[Fatal Main Exception] {ex}");
        }
        finally
        {
            _isExiting = true;
            try { _activationEvent?.Dispose(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
        }
    }
}
