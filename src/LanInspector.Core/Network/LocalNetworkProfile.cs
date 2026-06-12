using System.Net;

namespace LanInspector.Core.Network;

public sealed record LocalNetworkProfile(IReadOnlyList<LocalNetworkInterface> Interfaces)
{
    public LocalNetworkInterface? FindLocalInterface(IPAddress address)
    {
        return Interfaces.FirstOrDefault(item => item.Network.Contains(address));
    }

    public IPAddress? DefaultGateway => Interfaces.Select(item => item.GatewayAddress).FirstOrDefault(address => address is not null);
}

public sealed record LocalNetworkInterface(
    string Name,
    string Description,
    IPAddress Address,
    IPv4Network Network,
    IPAddress? GatewayAddress);
