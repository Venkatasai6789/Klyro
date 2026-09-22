using System;
using System.IO;
using System.Text.Json;

namespace Klyro.Core.Configuration;

/// <summary>
/// Loads and deserializes Klyro JSON configuration files with fail-closed validation.
/// </summary>
public static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static KlyroConfiguration LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Configuration file not found: '{filePath}'");
        }

        var json = File.ReadAllText(filePath);
        return LoadFromJson(json, filePath);
    }

    public static KlyroConfiguration LoadFromJson(string json, string sourceDescription = "raw json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        KlyroConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<KlyroConfiguration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse configuration JSON from {sourceDescription}: {ex.Message}", ex);
        }

        if (config == null)
        {
            throw new InvalidOperationException($"Configuration JSON from {sourceDescription} resulted in null object.");
        }

        var validationResult = ConfigurationValidator.Validate(config);
        if (!validationResult.IsValid)
        {
            var errors = string.Join(Environment.NewLine, validationResult.Errors);
            throw new InvalidOperationException($"Configuration validation failed for {sourceDescription}:{Environment.NewLine}{errors}");
        }

        return config;
    }
}
