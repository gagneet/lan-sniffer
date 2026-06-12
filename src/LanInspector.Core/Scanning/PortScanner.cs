using System.Net;
using System.Net.Sockets;

namespace LanInspector.Core.Scanning;

public sealed record PortScanResult(int Port, bool IsOpen, string ServiceName);

public sealed class PortScanner
{
    public static readonly IReadOnlyDictionary<int, string> CommonServices = new Dictionary<int, string>
    {
        [22] = "SSH",
        [23] = "Telnet",
        [53] = "DNS",
        [80] = "HTTP",
        [443] = "HTTPS",
        [445] = "SMB",
        [548] = "AFP",
        [631] = "IPP",
        [3389] = "RDP",
        [5000] = "App/Web",
        [8000] = "HTTP-alt",
        [8080] = "HTTP-alt",
        [8443] = "HTTPS-alt",
        [9100] = "Printer",
        [32400] = "Plex"
    };

    public async Task<IReadOnlyList<PortScanResult>> ScanCommonPortsAsync(
        IPAddress ipAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var tasks = CommonServices.Keys.Select(port => ScanPortAsync(ipAddress, port, timeout, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.Where(result => result.IsOpen).OrderBy(result => result.Port).ToArray();
    }

    private static async Task<PortScanResult> ScanPortAsync(
        IPAddress ipAddress,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(ipAddress.AddressFamily);
            var connectTask = client.ConnectAsync(ipAddress, port, cancellationToken).AsTask();
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed != connectTask)
            {
                return new PortScanResult(port, false, CommonServices[port]);
            }

            await connectTask;
            return new PortScanResult(port, client.Connected, CommonServices[port]);
        }
        catch
        {
            return new PortScanResult(port, false, CommonServices[port]);
        }
    }
}
