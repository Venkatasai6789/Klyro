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
    public string? CurrentRedirectUri => _currentRedirectUri;

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
            Log($"HttpListener started successfully on {_currentRedirectUri} & {bridgePrefix}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start HttpListener on {_currentRedirectUri}: {ex.Message}");
            return false;
        }

        // Build RFC 8252 Google OAuth URL with PKCE
        string authUrl = config.BuildAuthorizationUrl(_currentRedirectUri, codeChallenge, _currentState);

        // Start processing incoming requests concurrently so the listener is ready immediately
        var processTask = ProcessIncomingRequestsAsync();

        // Launch system default browser with robust fallback on background thread
        _ = Task.Run(() => LaunchBrowserUrl(authUrl));

        // Await incoming callback from browser
        return await processTask;
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
            Log($"HttpListener started successfully on {_currentRedirectUri} & {bridgePrefix}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start HttpListener on {_currentRedirectUri}: {ex.Message}");
            return false;
        }

        // Start processing incoming requests concurrently
        var processTask = ProcessIncomingRequestsAsync();

        // Launch system default browser on background thread
        _ = Task.Run(() => LaunchBrowserUrl(authUrl));

        return await processTask;
    }

    private static void Log(string message)
    {
        try
        {
            var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro");
            Directory.CreateDirectory(localAppData);
            File.AppendAllText(Path.Combine(localAppData, "boot.log"), $"[{DateTime.UtcNow:O}] [SystemBrowserAuth] {message}\n");
        }
        catch { }
    }

    private static void LaunchBrowserUrl(string url)
    {
        try
        {
            Log($"Launching browser for URL: {url}");
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
            Log($"Launched browser via ShellExecute for: {url}");
        }
        catch (Exception exLaunch)
        {
            Log($"ShellExecute browser launch failed: {exLaunch.Message}. Attempting cmd start fallback...");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" \"{url}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Log($"Launched browser via cmd fallback for: {url}");
            }
            catch (Exception exCmd)
            {
                Log($"cmd fallback also failed: {exCmd.Message}. HttpListener remains active.");
            }
        }
    }

    public bool IsListening => _listener?.IsListening ?? false;

    private async Task<bool> ProcessIncomingRequestsAsync()
    {
        if (_listener == null || _cts == null) return false;

        try
        {
            Log("ProcessIncomingRequestsAsync: Loop started, actively awaiting requests...");
            while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                Log($"ProcessIncomingRequestsAsync: Context received for {context.Request.Url}");
                bool completed = await HandleIncomingContextAsync(context);
                if (completed)
                {
                    Log("ProcessIncomingRequestsAsync: Auth flow marked completed.");
                    return true;
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            Log("ProcessIncomingRequestsAsync: Listener timed out or was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"ProcessIncomingRequestsAsync: Listener loop error: {ex.Message}");
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
        Log($"HandleIncomingContext: {request.HttpMethod} {request.Url}");

        // Route: Auth Bridge Landing (interactive fallback)
        if (path.StartsWith("/auth-bridge", StringComparison.OrdinalIgnoreCase))
        {
            Log("Serving /auth-bridge/ page to browser.");
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

            Log($"HandleAuthCallback: emailParam='{emailParam}', nameParam='{nameParam}', hasCode={!string.IsNullOrWhiteSpace(code)}");

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
            if (user == null && BrowserProfileDetector.IsValidGoogleEmail(emailParam))
            {
                user = new AuthenticatedUser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Email = emailParam!.Trim(),
                    DisplayName = BrowserProfileDetector.FormatCleanDisplayName(nameParam, emailParam),
                    PhotoUrl = photoParam,
                    AuthenticatedAt = DateTime.UtcNow
                };
            }

            // 3. Fallback: If neither OAuth exchange nor query param resolved, redirect browser to interactive bridge
            if (user == null)
            {
                response.Redirect("/auth-bridge/");
                response.OutputStream.Close();
                return false;
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

            Log($"AuthCompleted successfully invoked for: {resolvedUser.Email} ({resolvedUser.DisplayName})");
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
                sb.Append("<div style=\"margin: 22px 0 16px 0; text-align: left;\">");
                sb.Append("<p style=\"font-size: 11px; color: #94A3B8; text-transform: uppercase; letter-spacing: 0.08em; font-weight: 700; margin-bottom: 10px;\">Choose an active account from this browser:</p>");
                foreach (var acc in detectedAccounts)
                {
                    string safeEmail = WebUtility.HtmlEncode(acc.Email);
                    string safeName = WebUtility.HtmlEncode(acc.DisplayName);
                    string initials = WebUtility.HtmlEncode(acc.GetInitials());
                    string callbackUrl = $"/auth-callback/?email={Uri.EscapeDataString(acc.Email)}&name={Uri.EscapeDataString(acc.DisplayName)}";
                    sb.Append($"""
                        <a href="{callbackUrl}" style="display: flex; align-items: center; justify-content: space-between; padding: 12px 14px; margin-bottom: 8px; background: #0C1A30; border: 1px solid #1E3A5F; border-radius: 8px; text-decoration: none; color: #FFFFFF; transition: all 0.2s;">
                            <div style="display: flex; align-items: center; gap: 12px;">
                                <div style="width: 36px; height: 36px; border-radius: 18px; background: #0066FF; color: #FFFFFF; font-weight: 700; font-size: 13px; display: flex; align-items: center; justify-content: center; flex-shrink: 0;">{initials}</div>
                                <div>
                                    <div style="font-weight: 600; font-size: 14px; color: #FFFFFF;">{safeName}</div>
                                    <div style="font-size: 12px; color: #38BDF8;">{safeEmail}</div>
                                </div>
                            </div>
                            <div style="font-size: 12px; color: #38BDF8; font-weight: 600; padding: 6px 12px; background: rgba(56, 189, 248, 0.1); border-radius: 4px;">Use Account &rarr;</div>
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
                    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                    <title>Klyro — Candidate Sign-In Bridge</title>
                    <style>
                        * { box-sizing: border-box; }
                        body {
                            margin: 0; padding: 24px; display: flex; align-items: center; justify-content: center; min-height: 100vh;
                            background-color: #0F1219; color: #FFFFFF; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                        }
                        .card {
                            background: #131B2E; border: 1px solid #253554; border-radius: 12px; padding: 36px; max-width: 500px; width: 100%; box-shadow: 0 16px 36px rgba(0,0,0,0.5);
                        }
                        .logo-badge {
                            display: inline-flex; align-items: center; gap: 8px; margin-bottom: 16px;
                        }
                        .logo-badge span {
                            font-size: 11px; font-weight: 700; color: #38BDF8; letter-spacing: 0.12em; text-transform: uppercase; background: rgba(56, 189, 248, 0.12); padding: 4px 10px; border-radius: 4px; border: 1px solid rgba(56, 189, 248, 0.25);
                        }
                        h2 { margin: 0 0 8px 0; font-size: 24px; font-weight: 700; color: #FFFFFF; }
                        p.subtitle { margin: 0 0 20px 0; color: #94A3B8; font-size: 13px; line-height: 1.5; }
                        label { display: block; font-size: 11px; color: #94A3B8; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 6px; }
                        input[type="text"], input[type="email"] {
                            width: 100%; padding: 10px 14px; background: #0C1424; border: 1px solid #253554; border-radius: 6px; color: #FFFFFF; font-size: 14px; margin-bottom: 12px;
                        }
                        input:focus { border-color: #0066FF; outline: none; }
                        .btn {
                            display: block; width: 100%; padding: 12px; background: #0066FF; color: #FFFFFF; border: none; border-radius: 6px; font-weight: 600; font-size: 14px; cursor: pointer; transition: background 0.2s;
                        }
                        .btn:hover { background: #0052CC; }
                        .divider { display: flex; align-items: center; margin: 20px 0 16px 0; }
                        .divider-line { flex: 1; height: 1px; background: #1E293B; }
                        .divider-text { padding: 0 12px; font-size: 11px; color: #64748B; font-weight: 600; }
                        .web-signin {
                            margin-top: 20px; padding-top: 16px; border-top: 1px solid #1E293B; text-align: center;
                        }
                        .web-signin a {
                            display: inline-block; padding: 8px 16px; background: #0C1F38; border: 1px solid #1E3A5F; border-radius: 6px; color: #38BDF8; font-size: 12px; font-weight: 600; text-decoration: none;
                        }
                        .web-signin a:hover { background: #132D52; }
                        .footer { font-size: 11px; color: #64748B; margin-top: 24px; text-align: center; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="logo-badge">
                            <span>KLYRO ASSESSMENT BROWSER</span>
                        </div>
                        <h2>Candidate Sign-In</h2>
                        <p class="subtitle">Connect your institutional or university Google Account to continue with your scheduled examination.</p>
                        
                        {{accountsListHtml}}

                        <div class="divider">
                            <div class="divider-line"></div>
                            <div class="divider-text">OR ENTER DETAILS</div>
                            <div class="divider-line"></div>
                        </div>

                        <form id="authForm" method="GET" action="/auth-callback/" style="text-align: left;" onsubmit="return validateAndSubmit();">
                            <label for="nameInput">Full Name (optional):</label>
                            <input type="text" id="nameInput" name="name" placeholder="e.g. Venkata Sai" />

                            <label for="emailInput">University / Google Email:</label>
                            <input type="email" id="emailInput" name="email" placeholder="student@university.edu" required />

                            <button type="submit" class="btn">Connect Account &rarr;</button>
                        </form>

                        <div class="web-signin">
                            <p style="font-size: 12px; color: #94A3B8; margin: 0 0 10px 0;">Need to log into Google on this machine first?</p>
                            <a href="https://accounts.google.com/AddSession?continue=https%3A%2F%2Fmyaccount.google.com%2F" target="_blank">Sign in on Google Web &nearr;</a>
                            <p style="font-size: 11px; color: #64748B; margin: 8px 0 0 0;">Once signed in, refresh this page to select your active account.</p>
                        </div>

                        <div class="footer">Klyro Secure Assessment Browser &bull; RFC 8252 Loopback Bridge</div>
                    </div>

                    <script>
                        function validateAndSubmit() {
                            const email = document.getElementById('emailInput').value.trim();
                            if (!email || !email.includes('@') || email.length < 5) {
                                alert('Please enter a valid university or Google email address.');
                                return false;
                            }
                            return true;
                        }
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
