using System.Net;
using System.Text.Json;

namespace LanInspector.Core.RemoteAccess;

/// <summary>
/// Parses the JSON emitted by <c>tailscale status --json</c>. Kept separate from
/// <see cref="TailscaleCliService"/> so it can be exercised directly by tests against captured
/// output, rather than tests re-implementing the parse and drifting from production behaviour.
/// </summary>
public static class TailscaleStatusParser
{
    public static TailscaleStatus Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var backendState = root.TryGetProperty("BackendState", out var stateElement) ? stateElement.GetString() : null;
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
            }

            var selfName = string.Empty;
            var localIps = new List<IPAddress>();
            if (root.TryGetProperty("Self", out var selfElement))
            {
                selfName = ReadString(selfElement, "HostName") ?? string.Empty;
                localIps.AddRange(ParseIpArray(selfElement, "TailscaleIPs"));
            }

            var peers = new List<TailscaleDevice>();
            if (root.TryGetProperty("Peer", out var peersElement) && peersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peersElement.EnumerateObject())
                {
                    peers.Add(ParsePeer(peer.Value));
                }
            }

            return new TailscaleStatus(TailscaleConnectionState.Connected, peers, localIps, selfName);
        }
        catch (JsonException)
        {
            return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
        }
    }

    private static TailscaleDevice ParsePeer(JsonElement peer)
    {
        var hostname = ReadString(peer, "HostName") ?? string.Empty;
        var dnsName = ReadString(peer, "DNSName")?.TrimEnd('.') ?? string.Empty;
        var online = peer.TryGetProperty("Online", out var onlineElement)
            && onlineElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && onlineElement.GetBoolean();

        return new TailscaleDevice(
            hostname,
            dnsName,
            ParseIpArray(peer, "TailscaleIPs"),
            online,
            ParseEndpointArray(peer, "Addrs"),
            ParseEndpoint(ReadString(peer, "CurAddr")),
            ParsePeerApiAddresses(peer),
            ReadString(peer, "OS"),
            ReadTimestamp(peer, "LastSeen"),
            ReadStringArray(peer, "PrimaryRoutes"));
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static List<IPAddress> ParseIpArray(JsonElement element, string propertyName)
    {
        var result = new List<IPAddress>();
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && IPAddress.TryParse(item.GetString(), out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static List<IPEndPoint> ParseEndpointArray(JsonElement element, string propertyName)
    {
        var result = new List<IPEndPoint>();
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            var endpoint = ParseEndpoint(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
            if (endpoint is not null)
            {
                result.Add(endpoint);
            }
        }

        return result;
    }

    private static List<IPAddress> ParsePeerApiAddresses(JsonElement peer)
    {
        var result = new List<IPAddress>();
        if (!peer.TryGetProperty("PeerAPIURL", out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // Entries look like "http://192.168.0.154:37649" or "http://[fd7a::1]:37649".
            if (Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri)
                && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses Tailscale's <c>host:port</c> endpoint notation, including the bracketed IPv6 form.
    /// </summary>
    internal static IPEndPoint? ParseEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return IPEndPoint.TryParse(value.Trim(), out var endpoint) ? endpoint : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : null;
    }
}
