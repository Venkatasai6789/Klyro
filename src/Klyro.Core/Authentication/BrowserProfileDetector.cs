using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klyro.Core.Authentication;

/// <summary>
/// Discovers and extracts authentic Google Account identity profiles from local system browser profiles (Chrome, Edge, Brave).
/// Reads non-destructively using non-locking shared read access so active browser sessions can be inspected safely.
/// </summary>
public static class BrowserProfileDetector
{
    private static readonly string[] BrowserUserDataRelativePaths =
    [
        Path.Combine("Google", "Chrome", "User Data"),
        Path.Combine("Microsoft", "Edge", "User Data"),
        Path.Combine("BraveSoftware", "Brave-Browser", "User Data"),
        Path.Combine("Chromium", "User Data")
    ];

    private sealed class DetectedAccountCandidate
    {
        public required AuthenticatedUser User { get; init; }
        public DateTime LastModifiedUtc { get; init; }
        public string SourcePath { get; init; } = string.Empty;
    }

    /// <summary>
    /// Detects the most recently active authenticated Google Account across all local desktop browsers.
    /// </summary>
    public static AuthenticatedUser? DetectLatestGoogleAccount()
    {
        var candidates = DetectAllCandidates();
        return candidates.Count > 0 ? candidates[0].User : null;
    }

    /// <summary>
    /// Detects all Google Accounts found across local browser profiles, sorted with the most recently active first.
    /// </summary>
    public static IReadOnlyList<AuthenticatedUser> DetectAllGoogleAccounts()
    {
        return DetectAllCandidates()
            .Select(c => c.User)
            .GroupBy(u => u.Email.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
    }

    private static List<DetectedAccountCandidate> DetectAllCandidates()
    {
        var candidates = new List<DetectedAccountCandidate>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Directory.Exists(localAppData))
        {
            return candidates;
        }

        foreach (var relativePath in BrowserUserDataRelativePaths)
        {
            var fullBrowserPath = Path.Combine(localAppData, relativePath);
            if (!Directory.Exists(fullBrowserPath)) continue;

            try
            {
                var prefFiles = new List<string>();

                // Check default root Preferences
                var rootPref = Path.Combine(fullBrowserPath, "Default", "Preferences");
                if (File.Exists(rootPref)) prefFiles.Add(rootPref);

                // Check numbered profile directories ("Profile 1", "Profile 2", etc.)
                var subDirs = Directory.GetDirectories(fullBrowserPath, "Profile *", SearchOption.TopDirectoryOnly);
                foreach (var dir in subDirs)
                {
                    var pref = Path.Combine(dir, "Preferences");
                    if (File.Exists(pref)) prefFiles.Add(pref);
                }

                foreach (var prefFile in prefFiles)
                {
                    var parsed = ParsePreferencesFileInternal(prefFile);
                    candidates.AddRange(parsed);
                }
            }
            catch
            {
                // Silently skip inaccessible browser directories
            }
        }

        // Scan Mozilla Firefox profiles (logins.json)
        ScanFirefoxProfiles(candidates);

        // Sort descending by last modified time (most recent browser activity first)
        candidates.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
        return candidates;
    }

    /// <summary>
    /// Parses a single Preferences file safely, returning any Google accounts found.
    /// </summary>
    public static AuthenticatedUser? ParsePreferencesFile(string filePath)
    {
        var results = ParsePreferencesFileInternal(filePath);
        return results.Count > 0 ? results[0].User : null;
    }

    private static List<DetectedAccountCandidate> ParsePreferencesFileInternal(string filePath)
    {
        var candidates = new List<DetectedAccountCandidate>();
        if (!File.Exists(filePath)) return candidates;

        DateTime lastModifiedUtc;
        try
        {
            lastModifiedUtc = File.GetLastWriteTimeUtc(filePath);
        }
        catch
        {
            lastModifiedUtc = DateTime.MinValue;
        }

        try
        {
            // Open with FileShare.ReadWrite so active browser processes holding the file open do not block us
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var jsonDoc = JsonDocument.Parse(fileStream);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("account_info", out var accountInfoArray) ||
                accountInfoArray.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            foreach (var item in accountInfoArray.EnumerateArray())
            {
                if (!item.TryGetProperty("email", out var emailProp)) continue;
                var email = emailProp.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) continue;

                string? fullName = null;
                if (item.TryGetProperty("full_name", out var fnProp) && !string.IsNullOrWhiteSpace(fnProp.GetString()))
                {
                    fullName = fnProp.GetString()!.Trim();
                }

                if (string.IsNullOrWhiteSpace(fullName) && item.TryGetProperty("given_name", out var gnProp) && !string.IsNullOrWhiteSpace(gnProp.GetString()))
                {
                    fullName = gnProp.GetString()!.Trim();
                }

                // Check Edge-specific first/last name fields
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    string first = item.TryGetProperty("edge_account_first_name", out var ef) ? (ef.GetString()?.Trim() ?? "") : "";
                    string last = item.TryGetProperty("edge_account_last_name", out var el) ? (el.GetString()?.Trim() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last))
                    {
                        fullName = $"{first} {last}".Trim();
                    }
                }

                string? photoUrl = null;
                if (item.TryGetProperty("picture_url", out var picProp) && !string.IsNullOrWhiteSpace(picProp.GetString()))
                {
                    photoUrl = picProp.GetString()!.Trim();
                }
                else if (item.TryGetProperty("last_downloaded_image_url_with_size", out var imgProp) && !string.IsNullOrWhiteSpace(imgProp.GetString()))
                {
                    photoUrl = imgProp.GetString()!.Trim();
                }

                string? gaia = null;
                if (item.TryGetProperty("gaia", out var gaiaProp))
                {
                    gaia = gaiaProp.GetString()?.Trim();
                }

                var cleanDisplayName = FormatCleanDisplayName(fullName, email);

                var user = new AuthenticatedUser
                {
                    Id = gaia ?? string.Empty,
                    GoogleSub = gaia ?? string.Empty,
                    Email = email,
                    DisplayName = cleanDisplayName,
                    GivenName = fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                    FamilyName = fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).LastOrDefault(),
                    PhotoUrl = photoUrl,
                    AuthenticatedAt = lastModifiedUtc != DateTime.MinValue ? lastModifiedUtc : DateTime.UtcNow
                };

                candidates.Add(new DetectedAccountCandidate
                {
                    User = user,
                    LastModifiedUtc = lastModifiedUtc,
                    SourcePath = filePath
                });
            }
        }
        catch
        {
            // Silently ignore corrupted or unreadable JSON files
        }

        return candidates;
    }

    private static void ScanFirefoxProfiles(List<DetectedAccountCandidate> candidates)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) return;

            var firefoxProfilesDir = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(firefoxProfilesDir)) return;

            var profileDirs = Directory.GetDirectories(firefoxProfilesDir, "*", SearchOption.TopDirectoryOnly);
            foreach (var pDir in profileDirs)
            {
                var loginsFile = Path.Combine(pDir, "logins.json");
                if (!File.Exists(loginsFile)) continue;

                var parsed = ParseFirefoxLoginsFileInternal(loginsFile);
                candidates.AddRange(parsed);
            }
        }
        catch
        {
            // Silently ignore
        }
    }

    public static AuthenticatedUser? ParseFirefoxLoginsFile(string loginsFilePath)
    {
        var results = ParseFirefoxLoginsFileInternal(loginsFilePath);
        return results.Count > 0 ? results[0].User : null;
    }

    private static List<DetectedAccountCandidate> ParseFirefoxLoginsFileInternal(string loginsFilePath)
    {
        var candidates = new List<DetectedAccountCandidate>();
        if (!File.Exists(loginsFilePath)) return candidates;

        try
        {
            using var stream = new FileStream(loginsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("logins", out var loginsProp) && loginsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in loginsProp.EnumerateArray())
                {
                    string hostname = item.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";
                    if (!hostname.Contains("google.com", StringComparison.OrdinalIgnoreCase)) continue;

                    string username = item.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(username) || !username.Contains('@')) continue;

                    long timeLastUsed = item.TryGetProperty("timeLastUsed", out var tlu) && tlu.TryGetInt64(out var t) ? t : 0;
                    DateTime lastUsedUtc = timeLastUsed > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timeLastUsed).UtcDateTime
                        : File.GetLastWriteTimeUtc(loginsFilePath);

                    var user = new AuthenticatedUser
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Email = username.Trim(),
                        DisplayName = FormatCleanDisplayName(null, username.Trim()),
                        AuthenticatedAt = lastUsedUtc
                    };

                    candidates.Add(new DetectedAccountCandidate
                    {
                        User = user,
                        LastModifiedUtc = lastUsedUtc,
                        SourcePath = loginsFilePath
                    });
                }
            }
        }
        catch
        {
        }

        return candidates;
    }

    /// <summary>
    /// Formats a display name cleanly: title-cases words if lowercase, or derives a readable name from email username.
    /// </summary>
    public static string FormatCleanDisplayName(string? rawName, string email)
    {
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            var trimmed = rawName.Trim();
            // If rawName is already properly mixed case (e.g. "Hemanth Reddy", "P. Venkata Sai"), preserve it
            if (trimmed.Any(char.IsUpper) && trimmed.Any(char.IsLower))
            {
                return trimmed;
            }

            // If rawName is all lower or all upper, convert to title case
            return ToTitleCase(trimmed);
        }

        // Derive name from email user portion (e.g., "p.venkatsai333@gmail.com" -> "P Venkatsai")
        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@'))
        {
            var userPart = email.Split('@')[0];
            var clean = Regex.Replace(userPart, @"[\._\-0-9]+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(clean))
            {
                return ToTitleCase(clean);
            }
            return email.Split('@')[0];
        }

        return string.Empty;
    }

    private static string ToTitleCase(string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
            }
        }
        return string.Join(" ", words);
    }
}
