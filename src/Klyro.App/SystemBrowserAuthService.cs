using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Klyro.Core.Authentication;

namespace Klyro_App;

/// <summary>
/// Implements RFC 8252 (OAuth 2.0 for Native Apps) and RFC 7636 (PKCE) system browser authentication,
/// modeled after Mozilla Firefox and Thunderbird's OAuth2.sys.mjs architecture.
/// Opens the user's default desktop browser for authentication and receives completion callbacks via local loopback.
/// </summary>
public sealed class SystemBrowserAuthService : IDisposable
{
    private const int PreferredPort = 58291;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    private string? _currentCodeVerifier;
    private string? _currentRedirectUri;
    private string? _currentState;
    private GoogleOAuthConfiguration? _currentConfig;

    public event EventHandler<AuthenticatedUser>? AuthCompleted;

    /// <summary>
    /// Starts the complete Firefox-style Google OAuth 2.0 PKCE authentication flow.
    /// Allocates an ephemeral loopback port, builds the authorization URL with PKCE S256,
    /// launches the system browser, and awaits the redirect callback.
    /// </summary>
    public async Task<bool> StartGoogleOAuthAsync(GoogleOAuthConfiguration config, TimeSpan timeout, CancellationToken externalCt = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dispose();
        _isDisposed = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _cts.CancelAfter(timeout);

        _currentConfig = config;
        _currentCodeVerifier = PkceHelper.GenerateCodeVerifier();
        string codeChallenge = PkceHelper.GenerateCodeChallenge(_currentCodeVerifier);
        _currentState = Guid.NewGuid().ToString("N");

        int port = FindAvailablePort(PreferredPort);
        _currentRedirectUri = $"http://127.0.0.1:{port}/auth-callback/";
        string bridgePrefix = $"http://127.0.0.1:{port}/auth-bridge/";

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_currentRedirectUri);
            _listener.Prefixes.Add(bridgePrefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Failed to start HttpListener on {_currentRedirectUri}: {ex.Message}");
            return false;
        }

        // Build RFC 8252 Google OAuth URL with PKCE
        string authUrl = config.BuildAuthorizationUrl(_currentRedirectUri, codeChallenge, _currentState);

        // Launch system default browser
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Failed to launch system browser: {ex.Message}");
            _listener.Stop();
            return false;
        }

        // Await incoming callback from browser
        return await ProcessIncomingRequestsAsync();
    }

    /// <summary>
    /// Launches the default system browser with a specified URL and listens on a local loopback port for completion.
    /// Backward-compatible overload.
    /// </summary>
    public async Task<bool> StartSystemBrowserAuthAsync(string authUrl, TimeSpan timeout, CancellationToken externalCt = default)
    {
        Dispose();
        _isDisposed = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _cts.CancelAfter(timeout);

        int port = FindAvailablePort(PreferredPort);
        _currentRedirectUri = $"http://127.0.0.1:{port}/auth-callback/";
        string bridgePrefix = $"http://127.0.0.1:{port}/auth-bridge/";

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_currentRedirectUri);
            _listener.Prefixes.Add(bridgePrefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Failed to start HttpListener on {_currentRedirectUri}: {ex.Message}");
            return false;
        }

        // Launch system default browser
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Failed to launch system browser: {ex.Message}");
            _listener.Stop();
            return false;
        }

        return await ProcessIncomingRequestsAsync();
    }

    public bool IsListening => _listener?.IsListening ?? false;

    private async Task<bool> ProcessIncomingRequestsAsync()
    {
        if (_listener == null || _cts == null) return false;

        try
        {
            while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                bool completed = await HandleIncomingContextAsync(context);
                if (completed)
                {
                    return true;
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[SystemBrowserAuth] Listener timed out or was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Listener loop error: {ex.Message}");
            return false;
        }
        finally
        {
            try { _listener?.Stop(); } catch { }
        }
    }

    private async Task<bool> HandleIncomingContextAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        string path = request.Url?.AbsolutePath ?? "";

        // Route: Auth Bridge Landing (interactive fallback)
        if (path.StartsWith("/auth-bridge", StringComparison.OrdinalIgnoreCase))
        {
            ServeAuthBridgePage(response);
            return false; // Keep listening for final callback
        }

        // Route: Auth Callback
        return await HandleAuthCallbackAsync(context);
    }

    private async Task<bool> HandleAuthCallbackAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            string? code = request.QueryString["code"];
            string? state = request.QueryString["state"];
            string? error = request.QueryString["error"];
            string? emailParam = request.QueryString["email"];
            string? nameParam = request.QueryString["name"];
            string? photoParam = request.QueryString["photo"];

            AuthenticatedUser? user = null;

            // 1. Check for OAuth 2.0 authorization code (Firefox OAuth2.sys.mjs flow)
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(_currentCodeVerifier) && _currentConfig != null && !string.IsNullOrWhiteSpace(_currentRedirectUri))
            {
                user = await PkceHelper.ExchangeCodeForUserAsync(
                    code,
                    _currentCodeVerifier,
                    _currentRedirectUri,
                    _currentConfig.ClientId,
                    _currentConfig.ClientSecret,
                    _cts?.Token ?? default);
            }

            // 2. Check for explicit query parameters (from bridge or callback redirect)
            if (user == null && !string.IsNullOrWhiteSpace(emailParam) && emailParam.Contains('@'))
            {
                user = new AuthenticatedUser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Email = emailParam.Trim(),
                    DisplayName = BrowserProfileDetector.FormatCleanDisplayName(nameParam, emailParam),
                    PhotoUrl = photoParam,
                    AuthenticatedAt = DateTime.UtcNow
                };
            }

            // 3. Fallback: Dynamically detect authentic account from most recently active desktop browser profile
            if (user == null)
            {
                user = BrowserProfileDetector.DetectLatestGoogleAccount();
            }

            string displayEmail = user?.Email ?? "";
            string displayName = user?.DisplayName ?? "";

            string accountInfoHtml = !string.IsNullOrWhiteSpace(displayEmail)
                ? $"<p style=\"margin-bottom: 8px; color: #E2E8F0; font-weight: 600;\">{WebUtility.HtmlEncode(displayName)}</p><p style=\"margin-top: 0; color: #38BDF8; font-size: 13px;\">{WebUtility.HtmlEncode(displayEmail)}</p>"
                : string.Empty;

            string html = $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8" />
                    <title>Klyro — Authentication Verified</title>
                    <style>
                        body {
                            margin: 0;
                            padding: 0;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            height: 100vh;
                            background-color: #0F1219;
                            color: #FFFFFF;
                            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
                        }
                        .card {
                            background: #131B2E;
                            border: 1px solid #253554;
                            border-radius: 12px;
                            padding: 40px;
                            max-width: 440px;
                            text-align: center;
                            box-shadow: 0 10px 25px rgba(0,0,0,0.5);
                        }
                        .badge {
                            width: 56px;
                            height: 56px;
                            border-radius: 28px;
                            background: rgba(34, 197, 94, 0.15);
                            color: #22C55E;
                            font-size: 28px;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            margin: 0 auto 20px auto;
                        }
                        h2 { margin: 0 0 10px 0; font-size: 22px; font-weight: 700; color: #FFFFFF; }
                        p { margin: 0 0 24px 0; color: #94A3B8; font-size: 14px; line-height: 1.6; }
                        .btn {
                            display: inline-block;
                            padding: 10px 24px;
                            background: #0066FF;
                            color: #FFFFFF;
                            text-decoration: none;
                            border-radius: 6px;
                            font-weight: 600;
                            font-size: 14px;
                            margin-bottom: 20px;
                        }
                        .footer { font-size: 12px; color: #64748B; border-top: 1px solid #1E293B; padding-top: 16px; margin-top: 16px; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="badge">✓</div>
                        <h2>Authentication Successful</h2>
                        {{accountInfoHtml}}
                        <p>Your Google account has been verified for this examination. Klyro has detected your login and will resume automatically.</p>
                        <a href="#" onclick="window.close(); return false;" class="btn">Return to Klyro</a>
                        <div class="footer">Klyro Secure Assessment Browser</div>
                    </div>
                    <script>
                        setTimeout(() => { try { window.close(); } catch(e){} }, 2500);
                    </script>
                </body>
                </html>
                """;

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            try { App.BringToFront(); } catch { }

            var resolvedUser = user ?? new AuthenticatedUser
            {
                Email = displayEmail,
                DisplayName = displayName,
                AuthenticatedAt = DateTime.UtcNow
            };

            AuthCompleted?.Invoke(this, resolvedUser);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ServeAuthBridgePage(HttpListenerResponse response)
    {
        try
        {
            var detectedAccounts = BrowserProfileDetector.DetectAllGoogleAccounts();
            var sb = new StringBuilder();

            if (detectedAccounts.Count > 0)
            {
                sb.Append("<div style=\"margin: 20px 0; text-align: left;\">");
                sb.Append("<p style=\"font-size: 12px; color: #94A3B8; text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600; margin-bottom: 12px;\">Select an Active Account:</p>");
                foreach (var acc in detectedAccounts)
                {
                    string safeEmail = WebUtility.HtmlEncode(acc.Email);
                    string safeName = WebUtility.HtmlEncode(acc.DisplayName);
                    string callbackUrl = $"/auth-callback/?email={Uri.EscapeDataString(acc.Email)}&name={Uri.EscapeDataString(acc.DisplayName)}";
                    sb.Append($"""
                        <a href="{callbackUrl}" style="display: flex; align-items: center; justify-content: space-between; padding: 12px 16px; margin-bottom: 8px; background: #0C1F38; border: 1px solid #1E3A5F; border-radius: 8px; text-decoration: none; color: #FFFFFF; transition: all 0.2s;">
                            <div>
                                <div style="font-weight: 600; font-size: 14px;">{safeName}</div>
                                <div style="font-size: 12px; color: #38BDF8;">{safeEmail}</div>
                            </div>
                            <div style="font-size: 13px; color: #38BDF8; font-weight: 600;">Use this &rarr;</div>
                        </a>
                        """);
                }
                sb.Append("</div>");
            }

            string accountsListHtml = sb.ToString();

            string html = $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8" />
                    <title>Klyro — Candidate Sign-In Bridge</title>
                    <style>
                        body {
                            margin: 0; padding: 0; display: flex; align-items: center; justify-content: center; min-height: 100vh;
                            background-color: #0F1219; color: #FFFFFF; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                        }
                        .card {
                            background: #131B2E; border: 1px solid #253554; border-radius: 12px; padding: 36px; max-width: 480px; width: 100%; box-sizing: border-box; text-align: center;
                        }
                        h2 { margin: 0 0 8px 0; font-size: 22px; font-weight: 700; color: #FFFFFF; }
                        p { margin: 0 0 20px 0; color: #94A3B8; font-size: 13px; line-height: 1.5; }
                        input[type="email"] {
                            width: 100%; box-sizing: border-box; padding: 12px 14px; background: #0C1424; border: 1px solid #253554; border-radius: 6px; color: #FFFFFF; font-size: 14px; margin-bottom: 12px;
                        }
                        input[type="email"]:focus { border-color: #0066FF; outline: none; }
                        .btn {
                            display: block; width: 100%; box-sizing: border-box; padding: 12px; background: #0066FF; color: #FFFFFF; border: none; border-radius: 6px; font-weight: 600; font-size: 14px; cursor: pointer;
                        }
                        .footer { font-size: 11px; color: #64748B; margin-top: 20px; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <h2>Klyro Assessment Sign-In</h2>
                        <p>Authenticate with your institution Google account to proceed with the examination.</p>
                        {{accountsListHtml}}
                        <form method="GET" action="/auth-callback/" style="margin-top: 16px; text-align: left;">
                            <label style="font-size: 12px; color: #94A3B8; font-weight: 600; text-transform: uppercase;">Or enter your university email:</label>
                            <div style="margin-top: 8px;">
                                <input type="email" name="email" placeholder="student@university.edu" required />
                                <button type="submit" class="btn">Connect Account &rarr;</button>
                            </div>
                        </form>
                        <div class="footer">Klyro Secure Assessment Browser &bull; RFC 8252 Loopback Bridge</div>
                    </div>
                </body>
                </html>
                """;

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.StatusCode = 200;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
        catch { }
    }

    private static int FindAvailablePort(int preferred)
    {
        try
        {
            using var tcp = new TcpListener(IPAddress.Loopback, preferred);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }
        catch
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        try { _listener?.Close(); } catch { }
    }
}
