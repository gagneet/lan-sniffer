using System.Net;
using System.Net.Sockets;

namespace LanInspector.Core.Network;

public sealed record IPv4Network(IPAddress NetworkAddress, int PrefixLength)
{
    private readonly uint _network = ToUInt32(NetworkAddress);
    private readonly uint _mask = PrefixLength == 0 ? 0 : uint.MaxValue << (32 - PrefixLength);

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return (ToUInt32(address) & _mask) == _network;
    }

    public override string ToString()
    {
        return $"{NetworkAddress}/{PrefixLength}";
    }

    public static IPv4Network FromAddressAndPrefix(IPAddress address, int prefixLength)
    {
        var mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        var network = ToUInt32(address) & mask;
        return new IPv4Network(FromUInt32(network), prefixLength);
    }

    public static bool TryParse(string value, out IPv4Network? network)
    {
        network = null;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
        {
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork || prefix is < 0 or > 32)
        {
            return false;
        }

        network = FromAddressAndPrefix(address, prefix);
        return true;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24)
            | ((uint)bytes[1] << 16)
            | ((uint)bytes[2] << 8)
            | bytes[3];
    }

    private static IPAddress FromUInt32(uint address)
    {
        return new IPAddress(new[]
        {
            (byte)(address >> 24),
            (byte)(address >> 16),
            (byte)(address >> 8),
            (byte)address
        });
    }
}
