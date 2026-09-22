using System;
using System.IO;
using System.Threading.Tasks;
using Klyro.Core.Configuration;
using Klyro.Core.Health;
using Klyro.Core.Network;

namespace Klyro.Admin;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("=================================================");
        Console.WriteLine("  KLYRO — Secure Assessment Browser Admin CLI   ");
        Console.WriteLine("=================================================");
        Console.ResetColor();

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "diagnostics";

        try
        {
            switch (command)
            {
                case "diagnostics":
                case "--diag":
                    return await RunDiagnosticsAsync();

                case "validate-config":
                    string configPath = args.Length > 1 ? args[1] : "config/default.json";
                    return ValidateConfig(configPath);

                case "export":
                    string outputPath = args.Length > 1 ? args[1] : "klyro-diagnostics.md";
                    return await ExportDiagnosticsAsync(outputPath);

                default:
                    Console.WriteLine($"Unknown command: {command}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL ERROR] {ex.Message}");
            Console.ResetColor();
            return 2;
        }
    }

    private static async Task<int> RunDiagnosticsAsync()
    {
        Console.WriteLine("Loading system configuration...");
        var config = FindAndLoadConfig();

        Console.WriteLine("Running 10-point environmental health checks...");
        var checker = PreflightChecker.CreateDefault(config);
        var report = await checker.RunAllChecksAsync();

        Console.WriteLine();
        Console.WriteLine("--- Pre-flight Health Check Results ---");
        foreach (var result in report.Results)
        {
            var color = result.Status switch
            {
                HealthStatus.Pass => ConsoleColor.Green,
                HealthStatus.Warning => ConsoleColor.Yellow,
                HealthStatus.Fail => ConsoleColor.Red,
                _ => ConsoleColor.White
            };

            Console.ForegroundColor = color;
            string icon = result.Status == HealthStatus.Pass ? "[PASS]" : result.Status == HealthStatus.Warning ? "[WARN]" : "[FAIL]";
            Console.WriteLine($"  {icon,-6} {result.CheckName}: {result.Message}");
            Console.ResetColor();
        }

        Console.WriteLine();
        var snapshot = DiagnosticsSnapshot.Collect(config);
        Console.WriteLine(snapshot.ToMarkdown());

        return report.CanStartExam ? 0 : 1;
    }

    private static int ValidateConfig(string path)
    {
        Console.WriteLine($"Validating configuration file: {path}");
        if (!File.Exists(path))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"File not found: {path}");
            Console.ResetColor();
            return 1;
        }

        try
        {
            var config = ConfigurationLoader.LoadFromFile(path);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Configuration is VALID and strictly compliant with fail-closed security policies.");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Configuration is INVALID: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> ExportDiagnosticsAsync(string outputPath)
    {
        var config = FindAndLoadConfig();
        var snapshot = DiagnosticsSnapshot.Collect(config);
        await File.WriteAllTextAsync(outputPath, snapshot.ToMarkdown());

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Diagnostics successfully exported to: {Path.GetFullPath(outputPath)}");
        Console.ResetColor();
        return 0;
    }

    private static KlyroConfiguration FindAndLoadConfig()
    {
        string[] candidates = { "config/default.json", "../config/default.json", "../../config/default.json" };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return ConfigurationLoader.LoadFromFile(path);
        }
        return new KlyroConfiguration();
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/Klyro.Admin -- diagnostics");
        Console.WriteLine("  dotnet run --project src/Klyro.Admin -- validate-config <path>");
        Console.WriteLine("  dotnet run --project src/Klyro.Admin -- export [output-path]");
    }
}
