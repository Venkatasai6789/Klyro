using System.Threading.Tasks;

namespace Klyro.Core.Logging;

/// <summary>
/// Service interface for recording structured examination audit events.
/// </summary>
public interface IAuditLogger
{
    void Log(EventType eventType, string state, string details, string? targetUrl = null, string? examId = null);
    Task LogAsync(EventType eventType, string state, string details, string? targetUrl = null, string? examId = null);
}
