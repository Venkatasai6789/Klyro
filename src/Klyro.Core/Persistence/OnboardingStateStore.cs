using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Persistence;

/// <summary>
/// Record representing user onboarding and first-run completion status.
/// </summary>
public sealed class OnboardingState
{
    public bool IsFirstRunCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? GoogleEmail { get; set; }
    public string? GoogleDisplayName { get; set; }
    public string? GooglePhotoUrl { get; set; }
    public string? AppVersion { get; set; } = "1.0.0";
}

/// <summary>
/// Thread-safe local persistent store for first-run onboarding state.
/// </summary>
public sealed class OnboardingStateStore
{
    private readonly string _storagePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public OnboardingStateStore(string? customDirectory = null)
    {
        var baseDir = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klyro");

        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch { }

        _storagePath = Path.Combine(baseDir, "onboarding_state.json");
    }

    public bool IsFirstRunCompleted()
    {
        var state = LoadState();
        return state?.IsFirstRunCompleted ?? false;
    }

    public OnboardingState LoadState()
    {
        _fileLock.Wait();
        try
        {
            if (!File.Exists(_storagePath))
            {
                return new OnboardingState { IsFirstRunCompleted = false };
            }

            var json = File.ReadAllText(_storagePath);
            return JsonSerializer.Deserialize<OnboardingState>(json, JsonOptions) 
                   ?? new OnboardingState { IsFirstRunCompleted = false };
        }
        catch
        {
            return new OnboardingState { IsFirstRunCompleted = false };
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void SaveState(OnboardingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _fileLock.Wait();
        try
        {
            var tempPath = _storagePath + ".tmp";
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void MarkCompleted(string? googleEmail = null, string? displayName = null, string? photoUrl = null)
    {
        var state = LoadState();
        state.IsFirstRunCompleted = true;
        state.CompletedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(googleEmail)) state.GoogleEmail = googleEmail;
        if (!string.IsNullOrWhiteSpace(displayName)) state.GoogleDisplayName = displayName;
        if (!string.IsNullOrWhiteSpace(photoUrl)) state.GooglePhotoUrl = photoUrl;
        SaveState(state);
    }

    public void Reset()
    {
        _fileLock.Wait();
        try
        {
            if (File.Exists(_storagePath))
            {
                File.Delete(_storagePath);
            }
        }
        catch { }
        finally
        {
            _fileLock.Release();
        }
    }
}
