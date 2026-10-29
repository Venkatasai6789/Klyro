using System;
using System.Collections.Generic;

namespace Klyro.Core.Security.RemoteAccess;

/// <summary>
/// Dedicated detector for live screen-sharing indicators and remote desktop banner windows (Detection Layer C).
/// Inspects visible top-level windows for active indicators such as Chrome Remote Desktop's sharing banner,
/// TeamViewer session panels, AnyDesk active session frames, etc.
/// </summary>
public sealed class WindowBannerDetector
{
    private static readonly string[] ProhibitedWindowPhrases = new[]
    {
        "Your desktop is currently shared with",
        "Chrome Remote Desktop",
        "is sharing your screen",
        "is sharing a window",
        "Remote Desktop Connection",
        "TeamViewer Panel",
        "AnyDesk",
        "Quick Assist",
        "Remote Assistance",
        "RustDesk"
    };

    private readonly IWindowQueryProvider _windowProvider;

    public WindowBannerDetector(IWindowQueryProvider? windowProvider = null)
    {
        _windowProvider = windowProvider ?? new SystemWindowQueryProvider();
    }

    public RemoteAccessDetectionResult Evaluate()
    {
        try
        {
            var windows = _windowProvider.GetTopLevelWindows();
            foreach (var win in windows)
            {
                if (string.IsNullOrWhiteSpace(win.Title)) continue;

                foreach (var phrase in ProhibitedWindowPhrases)
                {
                    if (win.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RemoteAccessDetectionResult
                        {
                            Decision = SecurityPolicyDecision.ConfirmedViolation,
                            Category = DetectionCategory.ScreenSharing,
                            Classification = EvidenceClassification.ActiveProcess,
                            Details = $"Active remote desktop / screen sharing indicator window detected: \"{win.Title}\" ({win.ProcessName}).",
                            DetectedProcesses = !string.IsNullOrWhiteSpace(win.ProcessName) ? new[] { win.ProcessName } : Array.Empty<string>(),
                            Timestamp = DateTimeOffset.UtcNow
                        };
                    }
                }
            }

            return RemoteAccessDetectionResult.ClearResult();
        }
        catch (Exception ex)
        {
            return RemoteAccessDetectionResult.Unavailable($"Window banner inspection exception: {ex.Message}");
        }
    }
}
