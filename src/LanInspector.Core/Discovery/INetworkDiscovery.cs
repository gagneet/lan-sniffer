using System.Net;

namespace LanInspector.Core.Discovery;

public interface INetworkDiscovery
{
    Task<IReadOnlyCollection<IPAddress>> PingSweepAsync(
        IPAddress subnet,
        int cidr,
        CancellationToken cancellationToken = default);
}
