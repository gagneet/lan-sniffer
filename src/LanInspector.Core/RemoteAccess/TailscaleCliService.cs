using System.Net;
using System.Text.Json;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.RemoteAccess;

public sealed class TailscaleCliService : ITailscaleService
{
    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var tailscaleBin = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        var version = await ProcessHelper.RunAsync(tailscaleBin, "version", TimeSpan.FromSeconds(5), cancellationToken);
        if (string.IsNullOrWhiteSpace(version) || version.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []);
        }

        var statusJson = await ProcessHelper.RunAsync(tailscaleBin, "status --json", TimeSpan.FromSeconds(10), cancellationToken);
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
        }

        return ParseStatusJson(statusJson);
    }

    private static TailscaleStatus ParseStatusJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var backendState = root.TryGetProperty("BackendState", out var stateEl) ? stateEl.GetString() : null;
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
            }

            var selfName = string.Empty;
            var localIps = new List<IPAddress>();
            if (root.TryGetProperty("Self", out var selfEl))
            {
                selfName = selfEl.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? string.Empty : string.Empty;
                localIps.AddRange(ParseIpArray(selfEl, "TailscaleIPs"));
            }

            var peers = new List<TailscaleDevice>();
            if (root.TryGetProperty("Peer", out var peersEl))
            {
                foreach (var peer in peersEl.EnumerateObject())
                {
                    var hostname = peer.Value.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? string.Empty : string.Empty;
                    var dnsName = peer.Value.TryGetProperty("DNSName", out var dn) ? dn.GetString()?.TrimEnd('.') ?? string.Empty : string.Empty;
                    var online = peer.Value.TryGetProperty("Online", out var ol) && ol.GetBoolean();
                    var peerIps = ParseIpArray(peer.Value, "TailscaleIPs");
                    peers.Add(new TailscaleDevice(hostname, dnsName, peerIps, online));
                }
            }

            return new TailscaleStatus(TailscaleConnectionState.Connected, peers, localIps, selfName);
        }
        catch
        {
            return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
        }
    }

    private static List<IPAddress> ParseIpArray(JsonElement element, string propertyName)
    {
        var result = new List<IPAddress>();
        if (!element.TryGetProperty(propertyName, out var arrayEl))
        {
            return result;
        }

        foreach (var item in arrayEl.EnumerateArray())
        {
            if (IPAddress.TryParse(item.GetString(), out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }
}
