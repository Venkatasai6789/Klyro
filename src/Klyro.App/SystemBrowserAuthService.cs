using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro_App;

/// <summary>
/// Implements RFC 8252 (OAuth 2.0 for Native Apps) system browser authentication.
/// Opens the user's default desktop browser for authentication and receives completion callbacks via local loopback.
/// </summary>
public sealed class SystemBrowserAuthService : IDisposable
{
    private const int PreferredPort = 58291;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public event EventHandler<string>? AuthCompleted;

    /// <summary>
    /// Launches the default system browser and listens on a local loopback port for completion.
    /// </summary>
    public async Task<bool> StartSystemBrowserAuthAsync(string authUrl, TimeSpan timeout, CancellationToken externalCt = default)
    {
        Dispose();
        _isDisposed = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _cts.CancelAfter(timeout);

        int port = FindAvailablePort(PreferredPort);
        string loopbackPrefix = $"http://127.0.0.1:{port}/auth-callback/";

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(loopbackPrefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Failed to start HttpListener on {loopbackPrefix}: {ex.Message}");
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

        // Wait for loopback callback asynchronously
        try
        {
            var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            HandleIncomingCallback(context);
            return true;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[SystemBrowserAuth] Listener timed out or was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemBrowserAuth] Listener error: {ex.Message}");
            return false;
        }
        finally
        {
            try { _listener?.Stop(); } catch { }
        }
    }

    private void HandleIncomingCallback(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            string emailParam = request.QueryString["email"] ?? "Google User";

            string html = """
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
                        .footer { font-size: 12px; color: #64748B; border-top: 1px solid #1E293B; padding-top: 16px; margin-top: 16px; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="badge">✓</div>
                        <h2>Authentication Successful</h2>
                        <p>Your Google account has been successfully verified for this examination. You may now close this browser window and return to Klyro.</p>
                        <div class="footer">Klyro Secure Assessment Browser</div>
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

            AuthCompleted?.Invoke(this, emailParam);
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
