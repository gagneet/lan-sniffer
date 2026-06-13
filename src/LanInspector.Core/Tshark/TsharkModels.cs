namespace LanInspector.Core.Tshark;

public sealed record TsharkPacketSummary(
    int Number,
    double TimestampSeconds,
    string Source,
    string Destination,
    string Protocol,
    int Length,
    string Info);

public sealed record TsharkExportResult(
    string FilePath,
    int PacketCount,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null;
}
