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
        // Compare address bytes so numeric ordering is correct (avoids lexicographic string pitfall).
        var srcBytes = SourceIp.GetAddressBytes();
        var dstBytes = DestIp.GetAddressBytes();
        for (var i = 0; i < Math.Min(srcBytes.Length, dstBytes.Length); i++)
        {
            if (srcBytes[i] > dstBytes[i])
                return new TrafficFlowKey(DestIp, SourceIp, DestPort, SourcePort, Protocol);
            if (srcBytes[i] < dstBytes[i])
                break;
        }
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
