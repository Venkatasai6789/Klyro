using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klyro.Core.Configuration;
using Klyro.Core.Exam;
using Klyro.Core.Network;

namespace Klyro.Core.Health;

/// <summary>
/// Comprehensive diagnostic snapshot of the workstation and assessment environment.
/// Sanitized for safe export to university IT.
/// </summary>
public sealed class DiagnosticsSnapshot
{
    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("applicationName")]
    public string ApplicationName { get; init; } = "Klyro";

    [JsonPropertyName("applicationVersion")]
    public string ApplicationVersion { get; init; } = "1.0.0";

    [JsonPropertyName("environment")]
    public string EnvironmentName { get; init; } = "Production";

    [JsonPropertyName("machineName")]
    public string MachineName { get; init; } = Environment.MachineName;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; init; } = RuntimeInformation.OSDescription;

    [JsonPropertyName("osArchitecture")]
    public string OsArchitecture { get; init; } = RuntimeInformation.OSArchitecture.ToString();

    [JsonPropertyName("processArchitecture")]
    public string ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();

    [JsonPropertyName("dotnetVersion")]
    public string DotnetVersion { get; init; } = Environment.Version.ToString();

    [JsonPropertyName("networkAdapter")]
    public string? NetworkAdapter { get; init; }

    [JsonPropertyName("defaultGateway")]
    public string? DefaultGateway { get; init; }

    [JsonPropertyName("networkState")]
    public string NetworkState { get; init; } = "UNKNOWN";

    [JsonPropertyName("examState")]
    public string ExamState { get; init; } = "UNKNOWN";

    [JsonPropertyName("examProvider")]
    public string ExamProvider { get; init; } = "CodeChef";

    [JsonPropertyName("startUrl")]
    public string StartUrl { get; init; } = string.Empty;

    public static DiagnosticsSnapshot Collect(
        KlyroConfiguration config,
        Klyro.Core.Network.NetworkState currentNetworkState = Klyro.Core.Network.NetworkState.UNKNOWN,
        Klyro.Core.Exam.ExamState currentExamState = Klyro.Core.Exam.ExamState.INITIALIZING)
    {
        var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        var gateway = activeInterface?.GetIPProperties()?.GatewayAddresses
            .Select(g => g?.Address?.ToString())
            .FirstOrDefault(g => !string.IsNullOrEmpty(g));

        return new DiagnosticsSnapshot
        {
            ApplicationName = config.Application.Name,
            ApplicationVersion = config.Application.Version,
            EnvironmentName = config.Application.Environment,
            NetworkAdapter = activeInterface != null ? $"{activeInterface.Name} ({activeInterface.NetworkInterfaceType})" : "None",
            DefaultGateway = gateway ?? "None",
            NetworkState = currentNetworkState.ToString(),
            ExamState = currentExamState.ToString(),
            ExamProvider = config.Exam.Provider,
            StartUrl = config.Exam.StartUrl
        };
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        return $"""
        # Klyro Diagnostic Snapshot
        **Generated:** {TimestampUtc:yyyy-MM-dd HH:mm:ss UTC}  
        **Machine:** {MachineName}  
        **Application:** {ApplicationName} v{ApplicationVersion} ({EnvironmentName})  

        ## System Environment
        - **Operating System:** {OsVersion} ({OsArchitecture})
        - **Process Architecture:** {ProcessArchitecture}
        - **.NET Runtime:** {DotnetVersion}

        ## Network Diagnostics
        - **Active Interface:** {NetworkAdapter}
        - **Default Gateway:** {DefaultGateway}
        - **Network Engine State:** `{NetworkState}`

        ## Assessment Environment
        - **Provider:** {ExamProvider}
        - **Assessment URL:** `{StartUrl}`
        - **Exam Lifecycle State:** `{ExamState}`
        """;
    }
}
