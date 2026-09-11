using System.Net;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.RemoteAccess;

public enum TailscaleConnectionState
{
    NotInstalled,
    InstalledNotConnected,
    Connected
}

public sealed record TailscaleDevice(
    string Name,
    string DnsName,
    IReadOnlyList<IPAddress> TailscaleIps,
    bool IsOnline,
    IReadOnlyList<IPEndPoint>? Endpoints = null,
    IPEndPoint? CurrentAddress = null,
    IReadOnlyList<IPAddress>? PeerApiAddresses = null,
    string? OperatingSystem = null,
    DateTimeOffset? LastSeen = null)
{
    /// <summary>
    /// Endpoints Tailscale has learned for this peer. When a direct (non-DERP) path is up,
    /// <c>CurAddr</c> holds the address packets are actually flowing to.
    /// </summary>
    public IReadOnlyList<IPEndPoint> Endpoints { get; init; } = Endpoints ?? [];

    public IReadOnlyList<IPAddress> PeerApiAddresses { get; init; } = PeerApiAddresses ?? [];

    /// <summary>
    /// Private (RFC1918) addresses Tailscale associates with this peer, best evidence first:
    /// the active direct path, then the peer-API addresses, then the advertised endpoint list.
    /// This is what lets the app answer "which LAN IP is the server on right now?" without
    /// sweeping the subnet — Tailscale already knows, because it dialled the peer.
    /// </summary>
    public IReadOnlyList<IPAddress> LanAddressCandidates
    {
        get
        {
            var ordered = new List<IPAddress>();

            if (CurrentAddress is not null)
            {
                ordered.Add(CurrentAddress.Address);
            }

            ordered.AddRange(PeerApiAddresses);
            ordered.AddRange(Endpoints.Select(endpoint => endpoint.Address));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ordered
                .Where(RouteHelpers.IsRfc1918)
                .Where(address => seen.Add(address.ToString()))
                .ToArray();
        }
    }
}

public sealed record TailscaleStatus(
    TailscaleConnectionState State,
    IReadOnlyList<TailscaleDevice> Peers,
    IReadOnlyList<IPAddress> LocalIps,
    string? LocalName = null)
{
    /// <summary>
    /// Finds the peer matching any of the supplied names, comparing against both the short
    /// hostname and the fully-qualified MagicDNS name (with or without the trailing dot).
    /// </summary>
    public TailscaleDevice? FindPeerByName(IEnumerable<string> names)
    {
        var wanted = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().TrimEnd('.'))
            .ToArray();

        if (wanted.Length == 0)
        {
            return null;
        }

        return Peers.FirstOrDefault(peer => wanted.Any(name =>
            string.Equals(name, peer.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, peer.DnsName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name.Split('.', 2)[0], peer.Name, StringComparison.OrdinalIgnoreCase)));
    }
}
