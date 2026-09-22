using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Network;

/// <summary>
/// Production connectivity probe implementation utilizing HttpClient with deterministic timeouts.
/// </summary>
public sealed class ConnectivityProbe : IConnectivityProbe, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public ConnectivityProbe(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // Critical: do NOT auto-redirect, so we detect captive 302 intercepts!
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };

            _httpClient = new HttpClient(handler);
            _disposeClient = true;
        }
    }

    public async Task<ProbeResult> ExecuteAsync(ProbeType probeType, string endpoint, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var stopwatch = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stopwatch.Stop();

            int statusCode = (int)response.StatusCode;

            bool isSuccess = probeType switch
            {
                ProbeType.InternetConnectivity => statusCode == 204, // 204 No Content confirms unobstructed Internet
                ProbeType.CaptivePortal => statusCode is >= 200 and < 500, // Portal controller is responsive
                ProbeType.AssessmentProvider => statusCode is >= 200 and < 400, // Assessment site returns OK or valid redirect
                ProbeType.Gateway => statusCode is >= 200 and < 500,
                _ => statusCode == 200
            };

            if (isSuccess)
            {
                return ProbeResult.Success(probeType, endpoint, statusCode, stopwatch.ElapsedMilliseconds);
            }

            return ProbeResult.Failure(
                probeType,
                endpoint,
                $"Unexpected status code {statusCode} for probe type {probeType}",
                statusCode,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Failure(probeType, endpoint, $"Probe timed out after {timeoutMs}ms", 0, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Failure(probeType, endpoint, ex.Message, 0, stopwatch.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
