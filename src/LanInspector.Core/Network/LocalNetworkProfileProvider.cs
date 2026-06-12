using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanInspector.Core.Network;

public sealed class LocalNetworkProfileProvider : ILocalNetworkProfileProvider
{
    public LocalNetworkProfile GetCurrentProfile()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(CreateInterfaceProfiles)
            .ToArray();

        return new LocalNetworkProfile(interfaces);
    }

    private static IEnumerable<LocalNetworkInterface> CreateInterfaceProfiles(NetworkInterface networkInterface)
    {
        var properties = networkInterface.GetIPProperties();
        var gateway = properties.GatewayAddresses
            .Select(item => item.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(System.Net.IPAddress.Any));

        foreach (var address in properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var prefixLength = address.PrefixLength;
            if (prefixLength is < 1 or > 32)
            {
                continue;
            }

            yield return new LocalNetworkInterface(
                networkInterface.Name,
                networkInterface.Description,
                address.Address,
                IPv4Network.FromAddressAndPrefix(address.Address, prefixLength),
                gateway);
        }
    }
}
