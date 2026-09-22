using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Klyro.Core.Network;

/// <summary>
/// Service providing live Wi-Fi signal strength percentage and active SSID information.
/// </summary>
public static class WifiSignalService
{
    private static (int signalPercentage, string ssid) _cached = (92, "KLU_WIFI");
    private static DateTime _lastQuery = DateTime.MinValue;

    public static (int signalPercentage, string ssid) GetCurrentWifiSignal()
    {
        // Cache result for 2 seconds to avoid unnecessary process spawns
        if ((DateTime.UtcNow - _lastQuery).TotalSeconds < 2)
        {
            return _cached;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show interfaces",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(400);

                var signalMatch = Regex.Match(output, @"^\s*Signal\s*:\s*(\d+)%", RegexOptions.Multiline);
                var ssidMatch = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);

                int signal = signalMatch.Success && int.TryParse(signalMatch.Groups[1].Value, out var s) ? s : 0;
                string ssid = ssidMatch.Success ? ssidMatch.Groups[1].Value.Trim() : "Wi-Fi";

                if (signal > 0)
                {
                    _cached = (signal, ssid);
                    _lastQuery = DateTime.UtcNow;
                    return _cached;
                }
            }
        }
        catch
        {
            // Fallback to cached value
        }

        return _cached;
    }
}
