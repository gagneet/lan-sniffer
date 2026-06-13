namespace LanInspector.Core.Tshark;

public interface ITsharkService
{
    bool IsTsharkAvailable { get; }
    bool IsWiresharkAvailable { get; }
    string? TsharkPath { get; }
    string? WiresharkPath { get; }
    Task<TsharkExportResult> ExportPcapngAsync(string deviceName, TimeSpan duration, string outputPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TsharkPacketSummary>> ReadSummaryAsync(string pcapPath, CancellationToken cancellationToken = default);
    Task<bool> OpenInWiresharkAsync(string pcapPath, CancellationToken cancellationToken = default);
}
