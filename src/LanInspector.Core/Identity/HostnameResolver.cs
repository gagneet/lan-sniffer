using System.Net;
using System.Net.Sockets;
using SystemDns = System.Net.Dns;

namespace LanInspector.Core.Identity;

public sealed class HostnameResolver
{
    public async Task<string?> TryReverseDnsAsync(string ipAddress, TimeSpan timeout)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var entry = await SystemDns.GetHostEntryAsync(address.ToString(), AddressFamily.Unspecified, cts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Forward-resolves a hostname to its IPv4 addresses. Used to re-find a device by name
    /// (MagicDNS, the router's DHCP-registered name, or mDNS via <c>&lt;name&gt;.local</c>) after
    /// its address changed.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveIpv4Async(string hostname, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return [];
        }

        // An address literal resolves to itself; skip the DNS round trip.
        if (IPAddress.TryParse(hostname, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork ? [literal] : [];
        }

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var addresses = await SystemDns.GetHostAddressesAsync(hostname, AddressFamily.InterNetwork, cts.Token);
            return addresses;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}
