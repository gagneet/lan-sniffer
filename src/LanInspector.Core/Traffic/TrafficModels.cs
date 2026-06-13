using System.Net;

namespace LanInspector.Core.Traffic;

public sealed record TrafficFlowKey(
    IPAddress SourceIp,
    IPAddress DestIp,
    int SourcePort,
    int DestPort,
    string Protocol)
{
    public TrafficFlowKey Normalised()
    {
        // always put lower IP first so A->B and B->A merge
        if (SourceIp.ToString().CompareTo(DestIp.ToString()) > 0)
            return new TrafficFlowKey(DestIp, SourceIp, DestPort, SourcePort, Protocol);
        return this;
    }
}

public sealed class TrafficFlow
{
    public required TrafficFlowKey Key { get; init; }
    public long Packets { get; set; }
    public long Bytes { get; set; }
    public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public sealed class TrafficTimeBucket
{
    public required DateTime BucketStart { get; init; }
    public required TimeSpan BucketDuration { get; init; }
    public long Packets { get; set; }
    public long Bytes { get; set; }
}

public sealed class TrafficSummary
{
    public long TotalPackets { get; set; }
    public long TotalBytes { get; set; }
    public IReadOnlyList<TrafficFlow> TopFlows { get; init; } = [];
    public IReadOnlyList<TrafficTimeBucket> TimeSeries { get; init; } = [];
    public double PacketsPerSecond { get; set; }
    public double BytesPerSecond { get; set; }
}
