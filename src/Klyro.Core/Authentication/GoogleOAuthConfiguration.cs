using System;
using System.IO;
using System.Text.Json;

namespace Klyro.Core.Authentication;

/// <summary>
/// Configuration and URL builder for Google OAuth 2.0 PKCE authentication.
/// Modeled after Mozilla Thunderbird's OAuth2Providers.sys.mjs.
/// </summary>
public sealed class GoogleOAuthConfiguration
{
    public const string DefaultAuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string DefaultTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string DefaultUserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string DefaultScopes = "openid email profile";

    // Standard public client ID for desktop application authentication with loopback redirection (RFC 8252)
    // Institutional administrators can override this in config/default.json under authentication.googleClientId
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string AuthorizationEndpoint { get; set; } = DefaultAuthorizationEndpoint;
    public string TokenEndpoint { get; set; } = DefaultTokenEndpoint;
    public string Scopes { get; set; } = DefaultScopes;

    /// <summary>
    /// Checks whether a valid, non-placeholder Google Cloud OAuth Client ID is configured.
    /// Returns false if ClientId is missing, empty, or set to placeholder/revoked IDs (such as 406964657835...).
    /// </summary>
    public bool HasValidClientId =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !ClientId.Contains("406964657835", StringComparison.OrdinalIgnoreCase) &&
        !ClientId.StartsWith("dummy", StringComparison.OrdinalIgnoreCase) &&
        !ClientId.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads OAuth configuration from config/default.json or environment variables.
    /// </summary>
    public static GoogleOAuthConfiguration LoadFromEnvironmentOrConfig(string? configFilePath = null)
    {
        var config = new GoogleOAuthConfiguration();

        // 1. Check environment variables
        string? envClientId = Environment.GetEnvironmentVariable("KLYRO_GOOGLE_CLIENT_ID");
        string? envClientSecret = Environment.GetEnvironmentVariable("KLYRO_GOOGLE_CLIENT_SECRET");

        if (!string.IsNullOrWhiteSpace(envClientId))
        {
            config.ClientId = envClientId.Trim();
            config.ClientSecret = envClientSecret?.Trim();
            return config;
        }

        // 2. Check config file
        try
        {
            string path = configFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "default.json");
            if (!File.Exists(path))
            {
                // Try relative to workspace
                path = Path.Combine(Directory.GetCurrentDirectory(), "config", "default.json");
            }

            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("authentication", out var authSection))
                {
                    if (authSection.TryGetProperty("googleClientId", out var cid))
                    {
                        config.ClientId = cid.GetString()?.Trim() ?? string.Empty;
                    }
                    if (authSection.TryGetProperty("googleClientSecret", out var cs))
                    {
                        config.ClientSecret = cs.GetString()?.Trim();
                    }
                    if (authSection.TryGetProperty("scopes", out var sc))
                    {
                        config.Scopes = sc.GetString()?.Trim() ?? DefaultScopes;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal if config file cannot be parsed
        }

        return config;
    }

    /// <summary>
    /// Builds the authorization URL following RFC 8252 and RFC 7636 (PKCE S256).
    /// If no valid institutional Client ID is configured, routes cleanly to the local interactive loopback bridge (/auth-bridge/).
    /// </summary>
    public string BuildAuthorizationUrl(string redirectUri, string codeChallenge, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        // If no institutional Google Cloud OAuth Client ID is registered, route directly to the local
        // interactive bridge on loopback port so the candidate never encounters Google's Error 401 invalid_client.
        if (!HasValidClientId)
        {
            return redirectUri.Replace("/auth-callback/", "/auth-bridge/");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(AuthorizationEndpoint);
        sb.Append("?client_id=").Append(Uri.EscapeDataString(ClientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&response_type=code");
        sb.Append("&scope=").Append(Uri.EscapeDataString(Scopes));
        sb.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        sb.Append("&code_challenge_method=S256");
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        sb.Append("&access_type=offline");
        sb.Append("&prompt=select_account"); // Forces Google account chooser so the candidate can select their exact account

        return sb.ToString();
    }
}
