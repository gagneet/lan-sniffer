namespace LanInspector.Core.Model;

public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> GatewayAddresses);
