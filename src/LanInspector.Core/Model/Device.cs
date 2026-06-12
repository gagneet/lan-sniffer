namespace LanInspector.Core.Model;

public sealed class Device
{
    public required string MacAddress { get; init; }

    public HashSet<string> IpAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ObservedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SeenVia { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<int> OpenPorts { get; } = [];

    public Dictionary<int, string> PortServices { get; } = [];

    public string? Hostname { get; set; }

    public string? Vendor { get; set; }

    public string? DhcpVendorClass { get; set; }

    public string? Reachability { get; set; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; set; }

    public bool IsGateway { get; set; }

    public bool IsAccessPoint { get; set; }
}
