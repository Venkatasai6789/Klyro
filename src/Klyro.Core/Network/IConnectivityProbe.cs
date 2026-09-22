using System.Threading;
using System.Threading.Tasks;

namespace Klyro.Core.Network;

/// <summary>
/// Interface for running an individual connectivity probe.
/// </summary>
public interface IConnectivityProbe
{
    Task<ProbeResult> ExecuteAsync(ProbeType probeType, string endpoint, int timeoutMs, CancellationToken cancellationToken = default);
}
