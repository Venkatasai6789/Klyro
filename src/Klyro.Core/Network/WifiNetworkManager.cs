using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Klyro.Core.Network;

public sealed class WifiNetworkItem
{
    public string Ssid { get; set; } = string.Empty;
    public int SignalPercent { get; set; }
    public string Authentication { get; set; } = "Open";
    public string Encryption { get; set; } = "None";
    public List<string> Bssids { get; set; } = new();
    public bool IsConnected { get; set; }
    public bool IsCampusNetwork { get; set; }
    public bool HasProfile { get; set; }
    public bool IsOpen => string.Equals(Authentication, "Open", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(Encryption, "None", StringComparison.OrdinalIgnoreCase);

    public string SignalDisplay => $"{SignalPercent}%";
    public string SecurityDisplay => IsOpen ? "Open" : "Secured";
}

/// <summary>
/// Service managing Wi-Fi scanning, campus network recognition, autonomous connection, and network switching.
/// </summary>
public static class WifiNetworkManager
{
    // Regex matching university lab AP patterns: e.g. "8601A -  2", "8301B - 1", "8401 - A2", "8601B - 2"
    private static readonly Regex LabRoomPattern = new(@"^\d{4}[A-Za-z]?\s*-\s*\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Checks if an SSID matches known university campus and lab network patterns.
    /// </summary>
    public static bool IsUniversityCampusNetwork(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return false;

        string trimmed = ssid.Trim();

        // 1. Explicit university naming
        if (trimmed.Contains("KLU", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Kalasalingam", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. University lab AP naming convention: [RoomNumber] - [APNumber] (e.g. 8601A - 2, 8301B - 1)
        if (LabRoomPattern.IsMatch(trimmed))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scans available Wi-Fi networks using netsh wlan.
    /// </summary>
    public static async Task<List<WifiNetworkItem>> ScanNetworksAsync()
    {
        return await Task.Run(() =>
        {
            var networks = new Dictionary<string, WifiNetworkItem>(StringComparer.OrdinalIgnoreCase);
            var savedProfiles = GetSavedProfiles();
            var activeSsid = GetCurrentConnectedSsid();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan show networks mode=bssid",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

                    ParseNetworksOutput(output, networks, savedProfiles, activeSsid);
                }
            }
            catch
            {
                // Fallback to empty if netsh is not accessible
            }

            // Ensure currently connected network is present in list even if BSSID scan missed it
            if (!string.IsNullOrEmpty(activeSsid) && !networks.ContainsKey(activeSsid))
            {
                var currentSignal = WifiSignalService.GetCurrentWifiSignal();
                networks[activeSsid] = new WifiNetworkItem
                {
                    Ssid = activeSsid,
                    SignalPercent = currentSignal.signalPercentage > 0 ? currentSignal.signalPercentage : 90,
                    IsConnected = true,
                    IsCampusNetwork = IsUniversityCampusNetwork(activeSsid),
                    HasProfile = true
                };
            }

            // Order: Connected first, then Campus networks by signal descending, then remaining by signal descending
            return networks.Values
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.IsCampusNetwork)
                .ThenByDescending(n => n.SignalPercent)
                .ToList();
        });
    }

    /// <summary>
    /// Parses output of `netsh wlan show networks mode=bssid`.
    /// </summary>
    public static void ParseNetworksOutput(
        string output,
        Dictionary<string, WifiNetworkItem> networks,
        HashSet<string> savedProfiles,
        string? activeSsid)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        WifiNetworkItem? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            var ssidMatch = Regex.Match(line, @"^SSID\s+\d+\s*:\s*(.+)$");
            if (ssidMatch.Success)
            {
                string ssid = ssidMatch.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(ssid)) continue;

                if (!networks.TryGetValue(ssid, out current))
                {
                    current = new WifiNetworkItem
                    {
                        Ssid = ssid,
                        HasProfile = savedProfiles.Contains(ssid),
                        IsConnected = string.Equals(ssid, activeSsid, StringComparison.OrdinalIgnoreCase),
                        IsCampusNetwork = IsUniversityCampusNetwork(ssid)
                    };
                    networks[ssid] = current;
                }
                continue;
            }

            if (current == null) continue;

            var authMatch = Regex.Match(line, @"^Authentication\s*:\s*(.+)$");
            if (authMatch.Success)
            {
                current.Authentication = authMatch.Groups[1].Value.Trim();
                continue;
            }

            var encMatch = Regex.Match(line, @"^Encryption\s*:\s*(.+)$");
            if (encMatch.Success)
            {
                current.Encryption = encMatch.Groups[1].Value.Trim();
                continue;
            }

            var bssidMatch = Regex.Match(line, @"^BSSID\s+\d+\s*:\s*([0-9a-fA-F:]{17})");
            if (bssidMatch.Success)
            {
                var bssid = bssidMatch.Groups[1].Value.Trim().ToLowerInvariant();
                if (!current.Bssids.Contains(bssid))
                {
                    current.Bssids.Add(bssid);
                }
                continue;
            }

            var signalMatch = Regex.Match(line, @"^Signal\s*:\s*(\d+)%");
            if (signalMatch.Success && int.TryParse(signalMatch.Groups[1].Value, out var sig))
            {
                if (sig > current.SignalPercent)
                {
                    current.SignalPercent = sig;
                }
            }
        }
    }

    /// <summary>
    /// Autonomously searches for and connects to the best available university campus Wi-Fi network.
    /// Prefers KLU_WIFI or the strongest campus lab AP.
    /// </summary>
    public static async Task<bool> AutoConnectCampusWifiAsync()
    {
        var currentSsid = GetCurrentConnectedSsid();

        // If already connected to a university campus network, no reconnect needed
        if (!string.IsNullOrEmpty(currentSsid) && IsUniversityCampusNetwork(currentSsid))
        {
            return true;
        }

        var available = await ScanNetworksAsync();
        var campusCandidates = available
            .Where(n => n.IsCampusNetwork)
            .OrderByDescending(n => string.Equals(n.Ssid, "KLU_WIFI", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.SignalPercent)
            .ToList();

        if (campusCandidates.Count == 0)
        {
            return false;
        }

        var bestTarget = campusCandidates[0];
        return await ConnectToNetworkAsync(bestTarget.Ssid);
    }

    /// <summary>
    /// Connects to a specific Wi-Fi SSID, creating a profile if necessary.
    /// </summary>
    public static async Task<bool> ConnectToNetworkAsync(string ssid, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return false;

        return await Task.Run(() =>
        {
            try
            {
                var savedProfiles = GetSavedProfiles();

                // If profile doesn't exist, create and add it
                if (!savedProfiles.Contains(ssid))
                {
                    bool profileCreated = EnsureWifiProfileExists(ssid, password);
                    if (!profileCreated)
                    {
                        return false;
                    }
                }

                // Execute netsh wlan connect
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                proc.WaitForExit(3000);

                // Wait up to 5 seconds for connection to establish
                var startTime = DateTime.UtcNow;
                while ((DateTime.UtcNow - startTime).TotalSeconds < 5)
                {
                    System.Threading.Thread.Sleep(500);
                    var currentSsid = GetCurrentConnectedSsid();
                    if (string.Equals(currentSsid, ssid, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>
    /// Creates a temporary XML WLAN profile and registers it with Windows.
    /// </summary>
    public static bool EnsureWifiProfileExists(string ssid, string? password = null)
    {
        string profileXml;
        if (string.IsNullOrEmpty(password))
        {
            // Open unencrypted campus network profile (e.g. KLU_WIFI or lab AP)
            profileXml = GenerateOpenProfileXml(ssid);
        }
        else
        {
            // WPA2-Personal profile with security key
            profileXml = GenerateWpa2PskProfileXml(ssid, password);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"klyro_wlan_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempPath, profileXml);

            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"wlan add profile filename=\"{tempPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }
    }

    public static string GenerateOpenProfileXml(string ssid)
    {
        return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{ssid}</name>
    <SSIDConfig>
        <SSID>
            <name>{ssid}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>manual</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>open</authentication>
                <encryption>none</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
        </security>
    </MSM>
</WLANProfile>";
    }

    public static string GenerateWpa2PskProfileXml(string ssid, string password)
    {
        return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{ssid}</name>
    <SSIDConfig>
        <SSID>
            <name>{ssid}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>manual</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{password}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>";
    }

    public static HashSet<string> GetSavedProfiles()
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show profiles",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);

                var matches = Regex.Matches(output, @"All User Profile\s*:\s*(.+)$", RegexOptions.Multiline);
                foreach (Match m in matches)
                {
                    string profileName = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(profileName))
                    {
                        profiles.Add(profileName);
                    }
                }
            }
        }
        catch { }

        return profiles;
    }

    public static string? GetCurrentConnectedSsid()
    {
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
                proc.WaitForExit(1500);

                var stateMatch = Regex.Match(output, @"^\s*State\s*:\s*connected", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (stateMatch.Success)
                {
                    var ssidMatch = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (ssidMatch.Success)
                    {
                        return ssidMatch.Groups[1].Value.Trim();
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
