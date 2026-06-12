namespace LanInspector.Core.Capture;

public sealed record CaptureDeviceInfo(
    string Name,
    string Description,
    IReadOnlyList<string> Addresses);
