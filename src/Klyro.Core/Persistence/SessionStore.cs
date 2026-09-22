using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Persistence;

/// <summary>
/// File-based local session store with atomic writes for crash recovery.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private readonly string _storagePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SessionStore(string? customDirectory = null)
    {
        var baseDir = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klyro");

        Directory.CreateDirectory(baseDir);
        _storagePath = Path.Combine(baseDir, "session_recovery.json");
    }

    public void SaveSession(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _fileLock.Wait();
        try
        {
            var tempPath = _storagePath + ".tmp";
            var json = JsonSerializer.Serialize(record, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveSessionAsync(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _fileLock.WaitAsync();
        try
        {
            var tempPath = _storagePath + ".tmp";
            var json = JsonSerializer.Serialize(record, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public SessionRecord? LoadActiveSession()
    {
        _fileLock.Wait();
        try
        {
            if (!File.Exists(_storagePath)) return null;

            var json = File.ReadAllText(_storagePath);
            var record = JsonSerializer.Deserialize<SessionRecord>(json, JsonOptions);

            return (record != null && !record.IsCompleted) ? record : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SessionRecord?> LoadActiveSessionAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_storagePath)) return null;

            var json = await File.ReadAllTextAsync(_storagePath);
            var record = JsonSerializer.Deserialize<SessionRecord>(json, JsonOptions);

            return (record != null && !record.IsCompleted) ? record : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool HasActiveInterruptedSession()
    {
        var session = LoadActiveSession();
        return session != null && !session.IsCompleted;
    }

    public void ClearSession()
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
