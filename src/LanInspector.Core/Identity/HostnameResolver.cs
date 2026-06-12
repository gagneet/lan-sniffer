using System.Net;

namespace LanInspector.Core.Identity;

public sealed class HostnameResolver
{
    public async Task<string?> TryReverseDnsAsync(string ipAddress, TimeSpan timeout)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        try
        {
            var lookupTask = Dns.GetHostEntryAsync(address);
            var completed = await Task.WhenAny(lookupTask, Task.Delay(timeout));
            if (completed != lookupTask)
            {
                return null;
            }

            var entry = await lookupTask;
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch
        {
            return null;
        }
    }
}
