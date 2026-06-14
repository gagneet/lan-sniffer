using System.Net;

namespace LanInspector.Core.Topology;

public enum NetworkNodeType
{
    ThisMachine,
    Gateway,
    LocalDevice,
    RemoteDevice,
    Internet,
    WirelessIoT,  // Sub-GHz IoT device detected by Flipper
    WirelessAP,   // WiFi access point (Flipper WiFi dev board)
    Unknown
}

public enum TopologyLinkType
{
    Layer2,       // same subnet, ARP-visible
    Routed,       // different subnet, via gateway
    Tailscale,    // via Tailscale overlay
    SubnetRoute,  // Tailscale subnet route
    SubGhz,       // sub-GHz wireless (Flipper)
    Wireless      // 802.11 WiFi (Flipper WiFi dev board)
}

public enum TopologyConfidence
{
    Unknown,
    Low,          // pattern/heuristic only
    Medium,       // subnet + route evidence
    High,         // ARP + DHCP + route
    Confirmed     // SNMP / LLDP / local interface direct observation
}

public sealed class TopologyNode
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public NetworkNodeType Type { get; init; }
    public TopologyConfidence Confidence { get; init; }
    public IPAddress? PrimaryIp { get; init; }
    public string? MacAddress { get; init; }
    public string? Vendor { get; init; }
    public string? SubnetCidr { get; init; }
    public bool IsGateway { get; init; }
    public bool IsTailscale { get; init; }
    public bool IsCritical { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class TopologyEdge
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public TopologyLinkType LinkType { get; init; }
    public TopologyConfidence Confidence { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed class NetworkTopologySnapshot
{
    public required DateTime CapturedAt { get; init; }
    public IReadOnlyList<TopologyNode> Nodes { get; init; } = [];
    public IReadOnlyList<TopologyEdge> Edges { get; init; } = [];

    public TopologyNode? FindNode(string id) =>
        Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<TopologyNode> GetNeighbours(string nodeId) =>
        Edges
            .Where(e => string.Equals(e.FromId, nodeId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.ToId, nodeId, StringComparison.OrdinalIgnoreCase))
            .Select(e => string.Equals(e.FromId, nodeId, StringComparison.OrdinalIgnoreCase) ? e.ToId : e.FromId)
            .Select(FindNode)
            .Where(n => n is not null)
            .Cast<TopologyNode>()
            .ToList();

    public string ToMermaid()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("graph LR");
        foreach (var node in Nodes)
        {
            var shape = node.Type switch
            {
                NetworkNodeType.Gateway => $"[{node.DisplayName}]",
                NetworkNodeType.Internet => $"(({node.DisplayName}))",
                NetworkNodeType.ThisMachine => $"[{node.DisplayName}]",
                _ => $"[{node.DisplayName}]"
            };
            sb.AppendLine($"    {SanitizeId(node.Id)}{shape}");
        }
        foreach (var edge in Edges)
        {
            var arrow = edge.LinkType switch
            {
                TopologyLinkType.Tailscale => "-.->",
                TopologyLinkType.SubnetRoute => "==>",
                _ => "-->"
            };
            var label = edge.Label is not null ? $"|{edge.Label}|" : "";
            sb.AppendLine($"    {SanitizeId(edge.FromId)} {arrow}{label} {SanitizeId(edge.ToId)}");
        }
        return sb.ToString();
    }

    private static string SanitizeId(string id) =>
        System.Text.RegularExpressions.Regex.Replace(id, @"[^a-zA-Z0-9_]", "_");
}
