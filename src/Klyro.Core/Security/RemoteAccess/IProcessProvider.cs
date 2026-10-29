using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Klyro.Core.Security.RemoteAccess;

public record RunningProcessInfo(int Id, string ProcessName, string? MainWindowTitle, int SessionId = 1);

public interface IProcessProvider
{
    int CurrentSessionId { get; }
    IReadOnlyList<RunningProcessInfo> GetRunningProcesses();
}

public sealed class SystemProcessProvider : IProcessProvider
{
    private readonly int _currentSessionId;

    public int CurrentSessionId => _currentSessionId;

    public SystemProcessProvider()
    {
        try
        {
            _currentSessionId = Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            _currentSessionId = 1;
        }
    }

    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        var list = new List<RunningProcessInfo>();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    string name = proc.ProcessName;
                    string? title = null;
                    int sessionId = 0;
                    try { title = proc.MainWindowTitle; } catch { }
                    try { sessionId = proc.SessionId; } catch { }
                    list.Add(new RunningProcessInfo(proc.Id, name, title, sessionId));
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
        return list;
    }
}
