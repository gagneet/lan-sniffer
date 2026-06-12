using System.Net;
using System.Net.NetworkInformation;

namespace LanInspector.Core.Discovery;

public sealed class LocalSubnetDiscovery : INetworkDiscovery
{
    public async Task<IReadOnlyCollection<IPAddress>> PingSweepAsync(
        IPAddress subnet,
        int cidr,
        CancellationToken cancellationToken = default)
    {
        if (subnet.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 subnets are supported for the initial ping sweep.", nameof(subnet));
        }

        if (cidr is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(cidr), cidr, "CIDR must be between 1 and 30.");
        }

        var results = new List<IPAddress>();
        var addresses = ExpandHosts(subnet, cidr);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 64
        };

        await Parallel.ForEachAsync(addresses, options, async (ip, token) =>
        {
            using var ping = new Ping();

            try
            {
                var reply = await ping.SendPingAsync(ip, 500);
                if (reply.Status != IPStatus.Success || token.IsCancellationRequested)
                {
                    return;
                }

                lock (results)
                {
                    results.Add(ip);
                }
            }
            catch (PingException)
            {
                // Some hosts block ICMP or transiently reject probes.
            }
        });

        return results;
    }

    private static IEnumerable<IPAddress> ExpandHosts(IPAddress subnet, int cidr)
    {
        var baseAddress = ToUInt32(subnet);
        var mask = cidr == 0 ? 0u : uint.MaxValue << (32 - cidr);
        var network = baseAddress & mask;
        var broadcast = network | ~mask;

        for (var address = network + 1; address < broadcast; address++)
        {
            yield return FromUInt32(address);
        }
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
