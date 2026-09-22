using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Health;

/// <summary>
/// Interface for an individual pre-flight environmental check.
/// </summary>
public interface IPreflightCheck
{
    string Name { get; }
    bool IsCritical { get; }
    Task<HealthCheckResult> RunAsync(CancellationToken cancellationToken = default);
}
