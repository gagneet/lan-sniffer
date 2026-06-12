using System.Net;
using System.Net.Sockets;

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
            var entry = await Dns.GetHostEntryAsync(address.ToString(), AddressFamily.Unspecified, cts.Token);
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
}
