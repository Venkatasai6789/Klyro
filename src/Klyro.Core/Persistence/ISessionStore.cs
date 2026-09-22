using System.Threading.Tasks;

namespace Klyro.Core.Persistence;

/// <summary>
/// Interface for saving and retrieving minimal exam session recovery records.
/// </summary>
public interface ISessionStore
{
    void SaveSession(SessionRecord record);
    Task SaveSessionAsync(SessionRecord record);
    SessionRecord? LoadActiveSession();
    Task<SessionRecord?> LoadActiveSessionAsync();
    void ClearSession();
    bool HasActiveInterruptedSession();
}
