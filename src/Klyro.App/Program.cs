using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Klyro.Core.Security;

namespace Klyro_App;

public static class Program
{
    public const string WindowTitle = "Klyro Secure Assessment Browser";
    private const string MutexName = @"Local\Klyro_Secure_Assessment_Browser_SingleInstance";
    private const string ActivateEventName = @"Local\Klyro_Secure_Assessment_Browser_ActivateEvent";
    private const string ActivateAckEventName = @"Local\Klyro_Secure_Assessment_Browser_ActivateAckEvent";

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static EventWaitHandle? _activationAckEvent;
    private static volatile bool _isExiting;

    public static void SignalActivationAcknowledged()
    {
        try
        {
            _activationAckEvent?.Set();
        }
        catch { }
    }

    // WER: Disable Windows Error Reporting crash dialogs to prevent 0xc000027b terminations
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_RESTORE = 9;

    public static bool IsWindowHandleVisible(IntPtr hWnd)
    {
        try
        {
            return hWnd != IntPtr.Zero && IsWindowVisible(hWnd);
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreAndActivateWindow(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
        {
            try
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(hWnd);
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

        try
        {
            var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro");
            Directory.CreateDirectory(localAppData);
            File.AppendAllText(Path.Combine(localAppData, "boot.log"), $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch { }
    }

    [System.STAThread]
    public static void Main(string[] args)
    {
        // Suppress WER crash dialogs and native GPF error boxes at the OS level
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

        Log($"Main entered. PID: {Environment.ProcessId}. Args: {string.Join(" ", args)}");

        // Enforce GameDVR suppression and terminate background Game Bar processes
        try
        {
            KeyboardHookManager.ApplyGameDVRRestrictions();
            KeyboardHookManager.TerminateGameBarProcesses();
        }
        catch { }

        if (args.Any(a => a.Equals("--reset", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--reset-browser", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--reset-only", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var klyroDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro");
                if (Directory.Exists(klyroDir))
                {
                    var onboardingFile = Path.Combine(klyroDir, "onboarding_state.json");
                    if (File.Exists(onboardingFile)) File.Delete(onboardingFile);

                    var sessionFile = Path.Combine(klyroDir, "session_recovery.json");
                    if (File.Exists(sessionFile)) File.Delete(sessionFile);

                    var profilesDir = Path.Combine(klyroDir, "Profiles");
                    if (Directory.Exists(profilesDir)) Directory.Delete(profilesDir, recursive: true);
                }
                Log("Browser profiles, cookies, and onboarding state successfully reset via --reset switch.");
                if (args.Any(a => a.Equals("--reset-only", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }
            catch (Exception exReset)
            {
                Log($"Reset encountered error: {exReset.Message}");
            }
        }

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
                var ex = e.Exception;
                // Exclude benign task cancellations
                if (ex is OperationCanceledException) return;
                Log($"[FirstChance] {ex.GetType().FullName}: {ex.Message} \nSource: {ex.Source}\nStackTrace: {ex.StackTrace}");
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
                    bool activatedSuccessfully = false;
                    try
                    {
                        // Transfer foreground activation privilege to the running instance
                        AllowSetForegroundWindow((uint)others[0].Id);

                        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existingEvent))
                        {
                            existingEvent.Set();
                            existingEvent.Dispose();

                            // Wait up to 1200ms for primary instance to acknowledge window activation
                            if (EventWaitHandle.TryOpenExisting(ActivateAckEventName, out var ackEvent))
                            {
                                activatedSuccessfully = ackEvent.WaitOne(1200);
                                ackEvent.Dispose();
                            }
                        }
                    }
                    catch (Exception exSignal)
                    {
                        Log($"Activation signaling failed: {exSignal.Message}.");
                    }

                    if (activatedSuccessfully)
                    {
                        Log("Primary instance confirmed active window brought to front. Exiting secondary instance cleanly.");
                        return;
                    }

                    // Primary process did not acknowledge activation or has no visible window
                    Log($"Primary process PID {others[0].Id} did not acknowledge activation or had no visible window. Terminating stale background instance...");
                    foreach (var other in others)
                    {
                        try
                        {
                            other.Kill(entireProcessTree: true);
                            other.WaitForExit(1000);
                        }
                        catch { }
                    }
                    Log("Stale background process terminated. Proceeding as fresh primary instance.");
                }
                else
                {
                    Log("Mutex was held but no other active process was found. Continuing as primary instance.");
                }
            }

            // 2. Initialize Activation & Acknowledgment Listener Events
            try
            {
                _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _activationEvent.Reset();
                _activationAckEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateAckEventName);
                _activationAckEvent.Reset();

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
            try { _activationAckEvent?.Dispose(); } catch { }
            try { _activationEvent?.Dispose(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
        }
    }
}
