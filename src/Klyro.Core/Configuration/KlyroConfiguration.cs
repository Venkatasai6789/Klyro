using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Klyro.Core.Configuration;

/// <summary>
/// Root configuration schema for Klyro Secure Assessment Browser.
/// </summary>
public sealed class KlyroConfiguration
{
    [JsonPropertyName("application")]
    public ApplicationOptions Application { get; set; } = new();

    [JsonPropertyName("exam")]
    public ExamOptions Exam { get; set; } = new();

    [JsonPropertyName("network")]
    public NetworkOptions Network { get; set; } = new();

    [JsonPropertyName("security")]
    public SecurityOptions Security { get; set; } = new();

    [JsonPropertyName("browser")]
    public BrowserOptions Browser { get; set; } = new();
}

public sealed class ApplicationOptions
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Klyro";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "Production";
}

public sealed class ExamOptions
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "CodeChef";

    [JsonPropertyName("contestId")]
    public string? ContestId { get; set; }

    [JsonPropertyName("startUrl")]
    public string StartUrl { get; set; } = "https://www.codechef.com/";

    /// <summary>
    /// Timer policy: "ContinueDuringNetworkFailure" or "PauseDuringVerifiedNetworkFailure".
    /// </summary>
    [JsonPropertyName("timerPolicy")]
    public string TimerPolicy { get; set; } = "PauseDuringVerifiedNetworkFailure";

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; } = 120;
}

public sealed class NetworkOptions
{
    /// <summary>
    /// University captive portal endpoint.
    /// Default: http://172.16.103.254:8090/
    /// </summary>
    [JsonPropertyName("portalUrl")]
    public string PortalUrl { get; set; } = "http://172.16.103.254:8090/";

    [JsonPropertyName("probeIntervalMs")]
    public int ProbeIntervalMs { get; set; } = 2000;

    [JsonPropertyName("probeTimeoutMs")]
    public int ProbeTimeoutMs { get; set; } = 1500;

    [JsonPropertyName("failureThreshold")]
    public int FailureThreshold { get; set; } = 2;

    [JsonPropertyName("recoveryTimeoutMs")]
    public int RecoveryTimeoutMs { get; set; } = 60000;

    [JsonPropertyName("connectivityEndpoints")]
    public List<string> ConnectivityEndpoints { get; set; } = new()
    {
        "http://clients3.google.com/generate_204",
        "http://www.google.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt"
    };

    [JsonPropertyName("enableNetworkSimulator")]
    public bool EnableNetworkSimulator { get; set; } = false;
}

public sealed class SecurityOptions
{
    [JsonPropertyName("exitPin")]
    public string ExitPin { get; set; } = "123456";

    [JsonPropertyName("allowNewWindows")]
    public bool AllowNewWindows { get; set; } = false;

    [JsonPropertyName("allowNewTabs")]
    public bool AllowNewTabs { get; set; } = false;

    [JsonPropertyName("allowDownloads")]
    public bool AllowDownloads { get; set; } = false;

    [JsonPropertyName("allowPrinting")]
    public bool AllowPrinting { get; set; } = false;

    [JsonPropertyName("allowDevTools")]
    public bool AllowDevTools { get; set; } = false;

    [JsonPropertyName("allowExternalProtocols")]
    public bool AllowExternalProtocols { get; set; } = false;

    [JsonPropertyName("strictNavigationAllowlist")]
    public bool StrictNavigationAllowlist { get; set; } = true;

    [JsonPropertyName("clipboardPolicy")]
    public string ClipboardPolicy { get; set; } = "Restricted";
}

public sealed class BrowserOptions
{
    [JsonPropertyName("allowedOrigins")]
    public List<string> AllowedOrigins { get; set; } = new()
    {
        "https://www.codechef.com",
        "https://codechef.com",
        "https://cdn.codechef.com",
        "https://campus.codechef.com",
        "https://discuss.codechef.com",
        "https://accounts.google.com",
        "https://accounts.google.co.in",
        "https://myaccount.google.com",
        "https://myaccount.google.co.in",
        "https://ssl.gstatic.com",
        "https://fonts.googleapis.com",
        "https://fonts.gstatic.com",
        "https://www.gstatic.com",
        "https://apis.google.com",
        "https://accounts.youtube.com",
        "https://oauth2.googleapis.com"
    };

    [JsonPropertyName("recoveryAllowedOrigins")]
    public List<string> RecoveryAllowedOrigins { get; set; } = new()
    {
        "http://172.16.103.254:8090",
        "http://172.16.103.254"
    };

    [JsonPropertyName("approvedDownloadOrigins")]
    public List<string> ApprovedDownloadOrigins { get; set; } = new();
}
