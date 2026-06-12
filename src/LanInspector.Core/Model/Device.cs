namespace LanInspector.Core.Model;

public sealed class Device
{
    public required string MacAddress { get; init; }

    public HashSet<string> IpAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Hostname { get; set; }

    public string? Vendor { get; set; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; set; }

    public bool IsGateway { get; set; }

    public bool IsAccessPoint { get; set; }
}
