using System.Net;
using System.Net.Sockets;

namespace LanInspector.Core.Network;

/// <summary>
/// An IPv4 network, identified by its network address and prefix length.
/// </summary>
/// <remarks>
/// The constructor masks whatever address it is given down to the network address, so
/// <c>new IPv4Network(192.168.1.100, 24)</c> and <c>new IPv4Network(192.168.1.0, 24)</c> are the
/// same network. Previously it stored the address unmasked, and <see cref="Contains"/> then
/// matched nothing at all unless the caller had already masked it — which only
/// <see cref="FromAddressAndPrefix"/> did.
/// </remarks>
public sealed record IPv4Network
{
    private readonly uint _network;
    private readonly uint _mask;

    public IPv4Network(IPAddress networkAddress, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(networkAddress);

        if (networkAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses have an IPv4 network.", nameof(networkAddress));
        }

        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength), prefixLength, "Prefix length must be between 0 and 32.");
        }

        PrefixLength = prefixLength;
        _mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        _network = ToUInt32(networkAddress) & _mask;
        NetworkAddress = FromUInt32(_network);
    }

    public IPAddress NetworkAddress { get; }

    public int PrefixLength { get; }

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

    /// <summary>
    /// The network containing <paramref name="address"/> at the given prefix length. Equivalent to
    /// the constructor, which masks as well; kept because it reads better at call sites that start
    /// from a host address.
    /// </summary>
    public static IPv4Network FromAddressAndPrefix(IPAddress address, int prefixLength) =>
        new(address, prefixLength);

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
