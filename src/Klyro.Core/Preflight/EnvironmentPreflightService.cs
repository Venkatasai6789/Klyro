using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Klyro.Core.Authentication;
using Klyro.Core.Configuration;
using Klyro.Core.Logging;
using Klyro.Core.Network;

namespace Klyro.Core.Preflight;

/// <summary>
/// Production implementation of environment preflight engine.
/// Executes genuine, non-faked diagnostic checks with real telemetry.
/// </summary>
public sealed class EnvironmentPreflightService : IEnvironmentPreflightService
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
        "vboxtray"
    };

    private readonly KlyroConfiguration? _config;
    private readonly IConnectivityProbe _probe;
    private readonly IAuditLogger? _auditLogger;
    private readonly List<PreflightItemProgress> _items = new();
    private readonly object _lock = new();

    public event EventHandler<PreflightItemProgress>? ItemProgressChanged;

    public IReadOnlyList<PreflightItemProgress> CurrentItems
    {
        get
        {
            lock (_lock)
            {
                return _items.Select(i => i.Clone()).ToList();
            }
        }
    }

    public EnvironmentPreflightService(
        KlyroConfiguration? config = null,
        IConnectivityProbe? probe = null,
        IAuditLogger? auditLogger = null)
    {
        _config = config;
        _probe = probe ?? new ConnectivityProbe();
        _auditLogger = auditLogger;
        InitializeItems();
    }

    public void Reset()
    {
        lock (_lock)
        {
            InitializeItems();
        }
    }

    private void InitializeItems()
    {
        _items.Clear();
        _items.Add(new PreflightItemProgress
        {
            Id = PreflightCheckId.Account,
            Title = "Account",
            Subtitle = "Waiting for verification...",
            State = PreflightCheckState.Waiting
        });
        _items.Add(new PreflightItemProgress
        {
            Id = PreflightCheckId.Network,
            Title = "Network",
            Subtitle = "Waiting for network check...",
            State = PreflightCheckState.Waiting
        });
        _items.Add(new PreflightItemProgress
        {
            Id = PreflightCheckId.Internet,
            Title = "Internet",
            Subtitle = "Waiting for connection...",
            State = PreflightCheckState.Waiting
        });
        _items.Add(new PreflightItemProgress
        {
            Id = PreflightCheckId.AssessmentService,
            Title = "Assessment service",
            Subtitle = "Waiting for response...",
            State = PreflightCheckState.Waiting
        });
        _items.Add(new PreflightItemProgress
        {
            Id = PreflightCheckId.SecureEnvironment,
            Title = "Secure environment",
            Subtitle = "Waiting for checks to complete...",
            State = PreflightCheckState.Waiting
        });
    }

    public async Task<bool> RunAllChecksAsync(
        AuthenticatedUser? user,
        IProgress<PreflightProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int completed = 0;
        int total = _items.Count;

        for (int i = 0; i < _items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _items[i];
            UpdateItemState(item.Id, PreflightCheckState.Checking, GetCheckingSubtitle(item.Id));

            reportProgress();

            // Run genuine diagnostic
            var result = await ExecuteCheckInternalAsync(item.Id, user, cancellationToken);
            UpdateItemFromResult(item.Id, result);

            completed++;
            reportProgress();

            if (result.State == PreflightCheckState.Failed && item.IsCritical)
            {
                _auditLogger?.Log(EventType.SECURITY_VIOLATION, "PREFLIGHT_FAILED", $"Preflight check '{item.Title}' failed: {result.DiagnosticDetails}");
                return false;
            }
        }

        bool allPassed = _items.All(i => i.State is PreflightCheckState.Passed or PreflightCheckState.Warning);
        return allPassed;

        void reportProgress()
        {
            progress?.Report(new PreflightProgressReport
            {
                CompletedCount = _items.Count(i => i.State is PreflightCheckState.Passed or PreflightCheckState.Failed or PreflightCheckState.Warning),
                TotalCount = total,
                IsAllFinished = completed == total,
                AllPassed = _items.All(i => i.State is PreflightCheckState.Passed or PreflightCheckState.Warning),
                LatestUpdate = _items.FirstOrDefault(i => i.State == PreflightCheckState.Checking) ?? _items.LastOrDefault(i => i.State == PreflightCheckState.Passed)
            });
        }
    }

    public async Task<PreflightItemProgress> RerunCheckAsync(
        PreflightCheckId checkId,
        AuthenticatedUser? user,
        CancellationToken cancellationToken = default)
    {
        UpdateItemState(checkId, PreflightCheckState.Checking, GetCheckingSubtitle(checkId));
        var result = await ExecuteCheckInternalAsync(checkId, user, cancellationToken);
        UpdateItemFromResult(checkId, result);
        return result;
    }

    private static string GetCheckingSubtitle(PreflightCheckId id) => id switch
    {
        PreflightCheckId.Account => "Verifying account credentials...",
        PreflightCheckId.Network => "Inspecting network adapters...",
        PreflightCheckId.Internet => "Checking connection...",
        PreflightCheckId.AssessmentService => "Contacting assessment service...",
        PreflightCheckId.SecureEnvironment => "Scanning running processes...",
        _ => "Checking..."
    };

    private async Task<PreflightItemProgress> ExecuteCheckInternalAsync(
        PreflightCheckId checkId,
        AuthenticatedUser? user,
        CancellationToken cancellationToken)
    {
        switch (checkId)
        {
            case PreflightCheckId.Account:
                return CheckAccount(user);

            case PreflightCheckId.Network:
                return await CheckNetworkAsync();

            case PreflightCheckId.Internet:
                return await CheckInternetAsync(cancellationToken);

            case PreflightCheckId.AssessmentService:
                return await CheckAssessmentServiceAsync(cancellationToken);

            case PreflightCheckId.SecureEnvironment:
                return CheckSecureEnvironment();

            default:
                throw new ArgumentOutOfRangeException(nameof(checkId));
        }
    }

    private PreflightItemProgress CheckAccount(AuthenticatedUser? user)
    {
        if (user != null && !string.IsNullOrWhiteSpace(user.Email) && user.Email.Contains('@'))
        {
            bool isUniversityEmail = user.Email.EndsWith(".edu", StringComparison.OrdinalIgnoreCase) ||
                                     user.Email.Contains("klu.ac.in", StringComparison.OrdinalIgnoreCase) ||
                                     user.Email.Contains("university", StringComparison.OrdinalIgnoreCase);

            string subtitle = isUniversityEmail ? "University account verified" : $"{user.Email} verified";
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.Account,
                Title = "Account",
                Subtitle = subtitle,
                State = PreflightCheckState.Passed
            };
        }

        return new PreflightItemProgress
        {
            Id = PreflightCheckId.Account,
            Title = "Account",
            Subtitle = "Account unverified",
            State = PreflightCheckState.Failed,
            DiagnosticDetails = "No active authenticated Google session found. Please sign in to verify your identity."
        };
    }

    private async Task<PreflightItemProgress> CheckNetworkAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var activeInterface = interfaces.FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

                if (activeInterface == null)
                {
                    return new PreflightItemProgress
                    {
                        Id = PreflightCheckId.Network,
                        Title = "Network",
                        Subtitle = "No active network",
                        State = PreflightCheckState.Failed,
                        DiagnosticDetails = "No operational network adapter detected. Verify that Wi-Fi or Ethernet is enabled."
                    };
                }

                // If Wi-Fi adapter, try to resolve connected SSID
                if (activeInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    string? currentSsid = WifiNetworkManager.GetCurrentConnectedSsid();
                    if (!string.IsNullOrWhiteSpace(currentSsid))
                    {
                        bool isCampus = WifiNetworkManager.IsUniversityCampusNetwork(currentSsid);
                        string subtitle = isCampus ? "University Wi-Fi" : $"Wi-Fi ({currentSsid})";

                        return new PreflightItemProgress
                        {
                            Id = PreflightCheckId.Network,
                            Title = "Network",
                            Subtitle = subtitle,
                            State = PreflightCheckState.Passed
                        };
                    }

                    return new PreflightItemProgress
                    {
                        Id = PreflightCheckId.Network,
                        Title = "Network",
                        Subtitle = "Wi-Fi Connected",
                        State = PreflightCheckState.Passed
                    };
                }

                // Ethernet or other physical adapter
                string adapterName = string.IsNullOrWhiteSpace(activeInterface.Name) ? "Ethernet" : activeInterface.Name;
                return new PreflightItemProgress
                {
                    Id = PreflightCheckId.Network,
                    Title = "Network",
                    Subtitle = $"Ethernet ({adapterName})",
                    State = PreflightCheckState.Passed
                };
            }
            catch (Exception ex)
            {
                return new PreflightItemProgress
                {
                    Id = PreflightCheckId.Network,
                    Title = "Network",
                    Subtitle = "Adapter check error",
                    State = PreflightCheckState.Failed,
                    DiagnosticDetails = $"Failed to query network interfaces: {ex.Message}"
                };
            }
        });
    }

    private async Task<PreflightItemProgress> CheckInternetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probeResult = await _probe.ExecuteAsync(
                ProbeType.InternetConnectivity,
                "https://www.gstatic.com/generate_204",
                4000,
                cancellationToken);

            if (probeResult.IsSuccess)
            {
                return new PreflightItemProgress
                {
                    Id = PreflightCheckId.Internet,
                    Title = "Internet",
                    Subtitle = $"Connected ({probeResult.LatencyMs}ms)",
                    LatencyMs = probeResult.LatencyMs,
                    State = PreflightCheckState.Passed
                };
            }

            return new PreflightItemProgress
            {
                Id = PreflightCheckId.Internet,
                Title = "Internet",
                Subtitle = "Connection failed",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = probeResult.ErrorMessage ?? "Unable to reach global Internet probe endpoint."
            };
        }
        catch (OperationCanceledException)
        {
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.Internet,
                Title = "Internet",
                Subtitle = "Check timed out",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = "Internet connectivity probe timed out after 4 seconds."
            };
        }
        catch (Exception ex)
        {
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.Internet,
                Title = "Internet",
                Subtitle = "Probe error",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = $"Unexpected probe error: {ex.Message}"
            };
        }
    }

    private async Task<PreflightItemProgress> CheckAssessmentServiceAsync(CancellationToken cancellationToken)
    {
        string targetUrl = _config?.Exam?.StartUrl ?? "https://www.codechef.com";
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            targetUrl = "https://www.codechef.com";
        }

        try
        {
            var probeResult = await _probe.ExecuteAsync(
                ProbeType.AssessmentProvider,
                targetUrl,
                4000,
                cancellationToken);

            if (probeResult.IsSuccess)
            {
                return new PreflightItemProgress
                {
                    Id = PreflightCheckId.AssessmentService,
                    Title = "Assessment service",
                    Subtitle = "Service reachable",
                    LatencyMs = probeResult.LatencyMs,
                    State = PreflightCheckState.Passed
                };
            }

            return new PreflightItemProgress
            {
                Id = PreflightCheckId.AssessmentService,
                Title = "Assessment service",
                Subtitle = "Service unreachable",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = $"Could not contact assessment host at '{targetUrl}': {probeResult.ErrorMessage}"
            };
        }
        catch (OperationCanceledException)
        {
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.AssessmentService,
                Title = "Assessment service",
                Subtitle = "Service timeout",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = $"Assessment endpoint probe timed out after 4 seconds."
            };
        }
        catch (Exception ex)
        {
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.AssessmentService,
                Title = "Assessment service",
                Subtitle = "Service error",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = $"Failed to probe assessment service: {ex.Message}"
            };
        }
    }

    private PreflightItemProgress CheckSecureEnvironment()
    {
        try
        {
            var prohibited = new HashSet<string>(DefaultProhibitedProcesses, StringComparer.OrdinalIgnoreCase);
            if (_config?.Security?.BlockedProcesses != null)
            {
                foreach (var proc in _config.Security.BlockedProcesses)
                {
                    if (!string.IsNullOrWhiteSpace(proc))
                    {
                        prohibited.Add(proc.Trim());
                    }
                }
            }

            var currentProcesses = Process.GetProcesses();
            var detectedViolations = new List<string>();

            foreach (var proc in currentProcesses)
            {
                try
                {
                    string name = proc.ProcessName;
                    if (prohibited.Contains(name))
                    {
                        detectedViolations.Add(name);
                    }
                }
                catch
                {
                    // Ignore processes where process name access is denied
                }
            }

            if (detectedViolations.Count > 0)
            {
                string procList = string.Join(", ", detectedViolations.Distinct());
                return new PreflightItemProgress
                {
                    Id = PreflightCheckId.SecureEnvironment,
                    Title = "Secure environment",
                    Subtitle = "Security violation detected",
                    State = PreflightCheckState.Failed,
                    DiagnosticDetails = $"Prohibited software detected running in background: {procList}. Please terminate these applications to ensure examination integrity."
                };
            }

            return new PreflightItemProgress
            {
                Id = PreflightCheckId.SecureEnvironment,
                Title = "Secure environment",
                Subtitle = "Environment secure",
                State = PreflightCheckState.Passed
            };
        }
        catch (Exception ex)
        {
            return new PreflightItemProgress
            {
                Id = PreflightCheckId.SecureEnvironment,
                Title = "Secure environment",
                Subtitle = "Security check error",
                State = PreflightCheckState.Failed,
                DiagnosticDetails = $"Process scan exception: {ex.Message}"
            };
        }
    }

    private void UpdateItemState(PreflightCheckId checkId, PreflightCheckState state, string subtitle)
    {
        PreflightItemProgress? updated = null;
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == checkId);
            if (item != null)
            {
                item.State = state;
                item.Subtitle = subtitle;
                updated = item.Clone();
            }
        }

        if (updated != null)
        {
            ItemProgressChanged?.Invoke(this, updated);
        }
    }

    private void UpdateItemFromResult(PreflightCheckId checkId, PreflightItemProgress result)
    {
        PreflightItemProgress? updated = null;
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == checkId);
            if (item != null)
            {
                item.State = result.State;
                item.Subtitle = result.Subtitle;
                item.LatencyMs = result.LatencyMs;
                item.DiagnosticDetails = result.DiagnosticDetails;
                updated = item.Clone();
            }
        }

        if (updated != null)
        {
            ItemProgressChanged?.Invoke(this, updated);
        }
    }
}
