namespace LanInspector.Core.Model;

public sealed record NetworkSnapshot(
    DateTime CapturedAt,
    IReadOnlyCollection<Device> Devices,
    IReadOnlyCollection<NetworkInterfaceInfo> Interfaces);
