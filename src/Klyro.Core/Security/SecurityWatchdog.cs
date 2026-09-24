using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;

namespace Klyro.Core.Security;

public record SecurityViolation(string Rule, string Target, string Details, DateTimeOffset Timestamp);

/// <summary>
/// Autonomous anti-cheat security watchdog executing on a dedicated background OS thread.
/// Continuously monitors unauthorized background processes, remote desktop tools, screen recorders,
/// and virtual machine artifacts without consuming main UI thread cycles.
/// </summary>
public sealed class SecurityWatchdog : IDisposable
{
    private static readonly HashSet<string> DefaultProhibitedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord",
        "teamviewer",
        "anydesk",
        "obs64",
        "obs",
        "wireshark",
        "cheatengine",
        "processhacker",
        "fiddler",
        "x64dbg",
        "vmware",
        "vboxservice",
        "vboxtray",
        "gamebar",
        "gamebarft",
        "gamebarpresencewriter",
        "xboxgamingoverlay",
        "xboxgamingoverlayhost"
    };

    private readonly IAuditLogger? _auditLogger;
    private readonly HashSet<string> _prohibitedProcesses;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wakeUpEvent = new(false);

    private Thread? _watchdogThread;
    private bool _isDisposed;
    private volatile bool _isRunning;
    private readonly HashSet<string> _alreadyLoggedViolations = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _isRunning;
    public Thread? WorkerThread => _watchdogThread;
    public IReadOnlySet<string> ProhibitedProcesses => _prohibitedProcesses;

    public event EventHandler<SecurityViolation>? ViolationDetected;

    public SecurityWatchdog(
        SecurityOptions? securityOptions = null,
        IAuditLogger? auditLogger = null)
    {
        _auditLogger = auditLogger;
        _prohibitedProcesses = new HashSet<string>(DefaultProhibitedProcesses, StringComparer.OrdinalIgnoreCase);

        if (securityOptions?.BlockedProcesses != null)
        {
            foreach (var proc in securityOptions.BlockedProcesses)
            {
                if (!string.IsNullOrWhiteSpace(proc))
                {
                    _prohibitedProcesses.Add(proc.Trim());
                }
            }
        }
    }

    public void Start()
    {
        if (_watchdogThread != null) return;

        _isRunning = true;
        _watchdogThread = new Thread(RunWatchdogLoop)
        {
            Name = "Klyro_Security_Watchdog",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _watchdogThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _cts.Cancel();
        _wakeUpEvent.Set();
    }

    private void RunWatchdogLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                EvaluateRunningProcesses();

                // Wait 4 seconds between background scans
                _wakeUpEvent.Wait(4000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLogger?.Log(EventType.SECURITY_VIOLATION, "WATCHDOG_ERROR", $"Security watchdog loop exception: {ex.Message}");
            }
        }
    }

    private void EvaluateRunningProcesses()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    string procName = proc.ProcessName;
                    if (_prohibitedProcesses.Contains(procName))
                    {
                        if (_alreadyLoggedViolations.Add(procName))
                        {
                            var violation = new SecurityViolation(
                                "ProhibitedProcess",
                                procName,
                                $"Detected unauthorized background process: {procName} (PID: {proc.Id})",
                                DateTimeOffset.UtcNow);

                            _auditLogger?.Log(
                                EventType.SECURITY_VIOLATION,
                                "PROHIBITED_PROCESS",
                                violation.Details);

                            ViolationDetected?.Invoke(this, violation);
                        }
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        if (_watchdogThread != null && _watchdogThread.IsAlive)
        {
            _watchdogThread.Join(500);
        }

        _wakeUpEvent.Dispose();
        _cts.Dispose();
    }
}
