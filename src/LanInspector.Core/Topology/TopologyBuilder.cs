using System.Net;
using LanInspector.Core.Configuration;
using LanInspector.Core.Flipper;
using LanInspector.Core.Flipper.SubGhz;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;

namespace LanInspector.Core.Topology;

public sealed class TopologyBuilder
{
    private readonly List<TopologyNode> _nodes = [];
    private readonly List<TopologyEdge> _edges = [];
    private readonly HashSet<string> _nodeIds = new(StringComparer.OrdinalIgnoreCase);

    public TopologyBuilder AddLocalProfile(LocalNetworkProfile profile)
    {
        foreach (var iface in profile.Interfaces)
        {
            var nodeId = $"local:{iface.Name}";
            AddNode(new TopologyNode
            {
                Id = nodeId,
                DisplayName = $"This machine ({iface.Name})",
                Type = NetworkNodeType.ThisMachine,
                Confidence = TopologyConfidence.Confirmed,
                PrimaryIp = iface.Address,
                SubnetCidr = iface.Network.ToString(),
                Evidence = [$"Local interface: {iface.Name}", $"IP: {iface.Address}", $"Network: {iface.Network}"]
            });

            if (iface.GatewayAddress is not null)
            {
                var gwId = $"gw:{iface.GatewayAddress}";
                AddNode(new TopologyNode
                {
                    Id = gwId,
                    DisplayName = $"Gateway ({iface.GatewayAddress})",
                    Type = NetworkNodeType.Gateway,
                    Confidence = TopologyConfidence.High,
                    PrimaryIp = iface.GatewayAddress,
                    IsGateway = true,
                    Evidence = [$"Default gateway via {iface.Name}"]
                });

                AddEdge(new TopologyEdge
                {
                    FromId = nodeId,
                    ToId = gwId,
                    LinkType = TopologyLinkType.Routed,
                    Confidence = TopologyConfidence.High,
                    Label = "default route",
                    Evidence = [$"Interface {iface.Name} default gateway"]
                });
            }
        }

        return this;
    }

    public TopologyBuilder AddDiscoveredDevices(IEnumerable<Device> devices, LocalNetworkProfile profile)
    {
        foreach (var device in devices)
        {
            var ip = device.IpAddresses.FirstOrDefault();
            if (ip is null)
                continue;

            if (!IPAddress.TryParse(ip, out var ipAddr))
                continue;

            var nodeId = $"dev:{device.MacAddress}";
            var confidence = BuildConfidence(device);
            var evidence = BuildEvidence(device);

            AddNode(new TopologyNode
            {
                Id = nodeId,
                DisplayName = device.Hostname ?? device.IpAddresses.FirstOrDefault() ?? device.MacAddress,
                Type = device.IsGateway ? NetworkNodeType.Gateway : NetworkNodeType.LocalDevice,
                Confidence = confidence,
                PrimaryIp = ipAddr,
                MacAddress = device.MacAddress,
                Vendor = device.Vendor,
                IsGateway = device.IsGateway,
                Evidence = evidence
            });

            var localIface = profile.FindLocalInterface(ipAddr);
            var localNodeId = localIface is not null ? $"local:{localIface.Name}" : _nodes.FirstOrDefault(n => n.Type == NetworkNodeType.ThisMachine)?.Id;
            if (localNodeId is not null)
            {
                AddEdge(new TopologyEdge
                {
                    FromId = localNodeId,
                    ToId = nodeId,
                    LinkType = TopologyLinkType.Layer2,
                    Confidence = confidence,
                    Evidence = [.. device.SeenVia]
                });
            }
        }

        return this;
    }

    public TopologyBuilder AddKnownDevices(IEnumerable<KnownDeviceDefinition> devices, IEnumerable<Device> discovered)
    {
        var discoveredByIp = discovered
            .SelectMany(d => d.IpAddresses.Select(ip => (ip, d)))
            .ToDictionary(x => x.ip, x => x.d, StringComparer.OrdinalIgnoreCase);

        foreach (var known in devices)
        {
            var firstIp = known.KnownIps.FirstOrDefault();
            if (firstIp is null || !IPAddress.TryParse(firstIp, out var ipAddr))
                continue;

            // skip if already added from discovered
            if (discoveredByIp.ContainsKey(firstIp))
                continue;

            var nodeId = $"known:{known.Id}";
            AddNode(new TopologyNode
            {
                Id = nodeId,
                DisplayName = known.DisplayName,
                Type = NetworkNodeType.RemoteDevice,
                Confidence = TopologyConfidence.Low,
                PrimaryIp = ipAddr,
                IsCritical = known.IsCritical,
                Tags = [.. known.Tags],
                Evidence = [$"Known device config: {known.Id}"]
            });
        }

        return this;
    }

    public TopologyBuilder AddTailscaleStatus(TailscaleStatus tailscale)
    {
        if (tailscale.State != TailscaleConnectionState.Connected)
            return this;

        foreach (var peer in tailscale.Peers)
        {
            var peerIp = peer.TailscaleIps.FirstOrDefault();
            if (peerIp is null)
                continue;

            var nodeId = $"ts:{peer.Name}";
            if (!_nodeIds.Contains(nodeId))
            {
                AddNode(new TopologyNode
                {
                    Id = nodeId,
                    DisplayName = peer.Name,
                    Type = NetworkNodeType.RemoteDevice,
                    Confidence = TopologyConfidence.Confirmed,
                    PrimaryIp = peerIp,
                    IsTailscale = true,
                    Evidence = [$"Tailscale peer: {peer.DnsName}", $"Online: {peer.IsOnline}"]
                });
            }

            var localNodeId = _nodes.FirstOrDefault(n => n.Type == NetworkNodeType.ThisMachine)?.Id ?? "local";
            AddEdge(new TopologyEdge
            {
                FromId = localNodeId,
                ToId = nodeId,
                LinkType = TopologyLinkType.Tailscale,
                Confidence = TopologyConfidence.Confirmed,
                Label = "Tailscale",
                Evidence = [$"Tailscale overlay, peer online: {peer.IsOnline}"]
            });
        }

        return this;
    }

    /// <summary>
    /// Enrich the topology with IoT devices detected by the Flipper Zero sub-GHz radio.
    /// Each unique (frequency, protocol) combination becomes a WirelessIoT node linked
    /// to this machine with a SubGhz edge.
    /// </summary>
    public TopologyBuilder AddFlipperSubGhzDevices(SubGhzScanResult scanResult)
    {
        if (!scanResult.Succeeded || scanResult.Signals.Count == 0)
            return this;

        var localNodeId = _nodes.FirstOrDefault(n => n.Type == NetworkNodeType.ThisMachine)?.Id ?? "local";

        // Deduplicate by (protocol, frequency bucket) so one device doesn't appear N times
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in scanResult.Signals)
        {
            var proto  = signal.Protocol;
            var bucket = $"{signal.FrequencyMhz:F2}:{proto}:{signal.DecodedData ?? "raw"}";
            if (!seen.Add(bucket)) continue;

            var nodeId = $"flipper:subghz:{bucket}";
            var label  = $"{signal.ProtocolLabel} @ {signal.FrequencyLabel}";

            AddNode(new TopologyNode
            {
                Id          = nodeId,
                DisplayName = label,
                Type        = NetworkNodeType.WirelessIoT,
                Confidence  = proto == SubGhzProtocol.Unknown || proto == SubGhzProtocol.Raw
                                  ? TopologyConfidence.Low
                                  : TopologyConfidence.Medium,
                Evidence = BuildFlipperEvidence(signal)
            });

            AddEdge(new TopologyEdge
            {
                FromId     = localNodeId,
                ToId       = nodeId,
                LinkType   = TopologyLinkType.SubGhz,
                Confidence = TopologyConfidence.Low,
                Label      = $"{signal.FrequencyMhz:F3} MHz",
                Evidence   = [$"Flipper sub-GHz scan", $"RSSI: {signal.RssiDbm:F1} dBm"]
            });
        }

        return this;
    }

    private static IReadOnlyList<string> BuildFlipperEvidence(SubGhzSignal signal)
    {
        var ev = new List<string>
        {
            $"Detected by Flipper Zero sub-GHz radio",
            $"Frequency: {signal.FrequencyLabel}",
            $"RSSI: {signal.RssiDbm:F1} dBm",
            $"Protocol: {signal.ProtocolLabel}"
        };
        if (signal.DecodedData is not null)
            ev.Add($"Data: {signal.DecodedData}");
        ev.Add($"Detected at: {signal.DetectedAt:u}");
        return ev;
    }

    public NetworkTopologySnapshot Build() =>
        new()
        {
            CapturedAt = DateTime.UtcNow,
            Nodes = _nodes.AsReadOnly(),
            Edges = _edges.AsReadOnly()
        };

    private void AddNode(TopologyNode node)
    {
        if (_nodeIds.Add(node.Id))
            _nodes.Add(node);
    }

    private void AddEdge(TopologyEdge edge)
    {
        var key = $"{edge.FromId}|{edge.ToId}|{edge.LinkType}";
        if (!_edges.Any(e => $"{e.FromId}|{e.ToId}|{e.LinkType}" == key))
            _edges.Add(edge);
    }

    private static TopologyConfidence BuildConfidence(Device device)
    {
        if (device.SeenVia.Count == 0)
            return TopologyConfidence.Unknown;

        if (device.SeenVia.Contains("LLDP") || device.SeenVia.Contains("SNMP"))
            return TopologyConfidence.Confirmed;

        if (device.SeenVia.Contains("ARP") && (device.SeenVia.Contains("DHCP") || device.OpenPorts.Count > 0))
            return TopologyConfidence.High;

        if (device.SeenVia.Contains("ARP"))
            return TopologyConfidence.Medium;

        return TopologyConfidence.Low;
    }

    private static IReadOnlyList<string> BuildEvidence(Device device)
    {
        var evidence = new List<string>();
        if (device.SeenVia.Count > 0)
            evidence.Add($"Seen via: {string.Join(", ", device.SeenVia)}");
        if (device.Hostname is not null)
            evidence.Add($"Hostname: {device.Hostname}");
        if (device.Vendor is not null)
            evidence.Add($"Vendor: {device.Vendor}");
        if (device.OpenPorts.Count > 0)
            evidence.Add($"Open ports: {string.Join(", ", device.OpenPorts.OrderBy(p => p))}");
        if (device.DhcpVendorClass is not null)
            evidence.Add($"DHCP vendor class: {device.DhcpVendorClass}");
        return evidence;
    }
}
