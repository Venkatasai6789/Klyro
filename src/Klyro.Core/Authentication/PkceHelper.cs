using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Authentication;

/// <summary>
/// Implements RFC 7636 (Proof Key for Code Exchange / PKCE) and OpenID Connect ID token decoding
/// following the architecture used in Mozilla Firefox (OAuth2.sys.mjs).
/// </summary>
public static class PkceHelper
{
    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Generates a cryptographically random PKCE code verifier (RFC 7636 Section 4.1).
    /// Defaults to 32 bytes of entropy (yielding 43 URL-safe characters).
    /// </summary>
    public static string GenerateCodeVerifier(int byteCount = 32)
    {
        if (byteCount < 32 || byteCount > 96)
        {
            byteCount = 32;
        }

        byte[] bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Generates the SHA-256 PKCE code challenge from a code verifier (RFC 7636 Section 4.2).
    /// code_challenge = Base64Url(SHA256(ASCII(code_verifier)))
    /// </summary>
    public static string GenerateCodeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Encodes arbitrary bytes to a URL-safe Base64 string without '=' padding.
    /// </summary>
    public static string Base64UrlEncode(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    /// Decodes a URL-safe Base64 string (with or without padding) back to raw bytes.
    /// </summary>
    public static byte[] Base64UrlDecode(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        string incoming = input.Replace('-', '+').Replace('_', '/');
        switch (incoming.Length % 4)
        {
            case 2: incoming += "=="; break;
            case 3: incoming += "="; break;
        }

        return Convert.FromBase64String(incoming);
    }

    /// <summary>
    /// Decodes and extracts claims from a standard Google OpenID Connect ID Token (JWT).
    /// Validates structure and extracts real user attributes (email, name, picture, sub).
    /// </summary>
    public static AuthenticatedUser ParseGoogleIdToken(string idTokenJwt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idTokenJwt);

        var parts = idTokenJwt.Split('.');
        if (parts.Length < 2)
        {
            throw new FormatException("Invalid JWT token: Expected at least header and payload parts.");
        }

        byte[] payloadBytes = Base64UrlDecode(parts[1]);
        string json = Encoding.UTF8.GetString(payloadBytes);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string sub = GetStringProperty(root, "sub");
        string email = GetStringProperty(root, "email");
        string name = GetStringProperty(root, "name");
        string givenName = GetStringProperty(root, "given_name");
        string familyName = GetStringProperty(root, "family_name");
        string picture = GetStringProperty(root, "picture");
        string hd = GetStringProperty(root, "hd");
        bool emailVerified = root.TryGetProperty("email_verified", out var ev) && ev.GetBoolean();

        return new AuthenticatedUser
        {
            Id = !string.IsNullOrWhiteSpace(sub) ? sub : Guid.NewGuid().ToString("N"),
            GoogleSub = sub,
            Email = email,
            DisplayName = BrowserProfileDetector.FormatCleanDisplayName(name, email),
            GivenName = givenName,
            FamilyName = familyName,
            PhotoUrl = !string.IsNullOrWhiteSpace(picture) ? picture : null,
            VerifiedEmail = emailVerified,
            HostedDomain = !string.IsNullOrWhiteSpace(hd) ? hd : null,
            AuthenticatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Exchanges a Google OAuth 2.0 authorization code for an ID token and profile claims
    /// using PKCE verification (RFC 8252 / RFC 7636).
    /// </summary>
    public static async Task<AuthenticatedUser?> ExchangeCodeForUserAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        string? clientSecret = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            };

            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                parameters["client_secret"] = clientSecret;
            }

            using var content = new FormUrlEncodedContent(parameters);
            var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // 1. Try parsing id_token (OIDC JWT)
            if (root.TryGetProperty("id_token", out var idTokenProp))
            {
                string? idToken = idTokenProp.GetString();
                if (!string.IsNullOrWhiteSpace(idToken))
                {
                    return ParseGoogleIdToken(idToken);
                }
            }

            // 2. Fallback: Query Google userinfo endpoint if access_token was returned
            if (root.TryGetProperty("access_token", out var accessTokenProp))
            {
                string? accessToken = accessTokenProp.GetString();
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    using var userinfoReq = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
                    userinfoReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    var userinfoResp = await HttpClient.SendAsync(userinfoReq, ct);

                    if (userinfoResp.IsSuccessStatusCode)
                    {
                        string userinfoJson = await userinfoResp.Content.ReadAsStringAsync(ct);
                        using var uiDoc = JsonDocument.Parse(userinfoJson);
                        var uiRoot = uiDoc.RootElement;

                        return new AuthenticatedUser
                        {
                            Id = GetStringProperty(uiRoot, "sub"),
                            GoogleSub = GetStringProperty(uiRoot, "sub"),
                            Email = GetStringProperty(uiRoot, "email"),
                            DisplayName = BrowserProfileDetector.FormatCleanDisplayName(GetStringProperty(uiRoot, "name"), GetStringProperty(uiRoot, "email")),
                            GivenName = GetStringProperty(uiRoot, "given_name"),
                            FamilyName = GetStringProperty(uiRoot, "family_name"),
                            PhotoUrl = GetStringProperty(uiRoot, "picture"),
                            AuthenticatedAt = DateTime.UtcNow
                        };
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
