namespace LanInspector.UI.ViewModels;

public sealed class CaptureRunHistoryItemViewModel
{
    public CaptureRunHistoryItemViewModel(
        int runNumber,
        DateTime capturedAt,
        long packetCount,
        int deviceCount,
        string filter,
        IEnumerable<DeviceRowViewModel> devices)
    {
        RunNumber = runNumber;
        CapturedAt = capturedAt;
        PacketCount = packetCount;
        DeviceCount = deviceCount;
        Filter = filter;
        Devices = devices
            .Select(device => string.IsNullOrWhiteSpace(device.DisplayName) ? device.MacAddress : device.DisplayName)
            .Take(8)
            .ToArray();
    }

    public int RunNumber { get; }

    public DateTime CapturedAt { get; }

    public long PacketCount { get; }

    public int DeviceCount { get; }

    public string Filter { get; }

    public IReadOnlyList<string> Devices { get; }

    public string Summary => $"Run {RunNumber} - {DeviceCount} device(s), {PacketCount} packet(s)";

    public string CapturedAtLocal => CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string DevicePreview => Devices.Count == 0 ? "No devices captured" : string.Join(", ", Devices);
}
