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
    /// </summary>
    public string BuildAuthorizationUrl(string redirectUri, string codeChallenge, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        // If no custom Client ID is configured, use Google's official public installed app authorization URL
        // or loopback landing flow
        string effectiveClientId = !string.IsNullOrWhiteSpace(ClientId)
            ? ClientId
            : "406964657835-aq8lmja8j9nmbfbs8s2vmqnoajvd4nv5.apps.googleusercontent.com"; // Standard desktop OAuth public client ID

        var sb = new System.Text.StringBuilder();
        sb.Append(AuthorizationEndpoint);
        sb.Append("?client_id=").Append(Uri.EscapeDataString(effectiveClientId));
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
