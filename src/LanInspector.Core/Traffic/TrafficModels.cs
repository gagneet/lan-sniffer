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

    public double BytesPerSecond => BucketDuration.TotalSeconds > 0 ? Bytes / BucketDuration.TotalSeconds : 0;
}

/// <summary>
/// The time span a traffic view covers. Each window names the resolution it is served at: the
/// live view is second-by-second, while the longer views are served from minute rollups so that
/// an hour of history costs sixty buckets rather than three and a half thousand.
/// </summary>
public enum TrafficWindow
{
    LastMinute,
    LastFifteenMinutes,
    LastHour,
    LastThreeHours
}

public static class TrafficWindows
{
    public static TimeSpan GetBucketDuration(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastMinute => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromMinutes(1)
    };

    public static int GetBucketCount(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastMinute => 60,
        TrafficWindow.LastFifteenMinutes => 15,
        TrafficWindow.LastHour => 60,
        TrafficWindow.LastThreeHours => 180,
        _ => 60
    };

    public static TimeSpan GetDuration(this TrafficWindow window) =>
        window.GetBucketDuration() * window.GetBucketCount();

    public static string GetLabel(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastMinute => "Last 60 seconds",
        TrafficWindow.LastFifteenMinutes => "Last 15 minutes",
        TrafficWindow.LastHour => "Last 60 minutes",
        TrafficWindow.LastThreeHours => "Last 3 hours",
        _ => window.ToString()
    };
}

/// <summary>
/// One conversation's share of a single time bucket — the answer to "what was using the network
/// at 10:42?".
/// </summary>
public sealed record TrafficBucketContributor(
    string Source,
    string Destination,
    string Protocol,
    long Bytes,
    long Packets);

/// <summary>
/// What a single bar on the throughput chart is made of.
/// </summary>
public sealed record TrafficBucketDetail(
    DateTime BucketStart,
    TimeSpan BucketDuration,
    long Bytes,
    long Packets,
    IReadOnlyList<TrafficBucketContributor> Contributors)
{
    public double BytesPerSecond => BucketDuration.TotalSeconds > 0 ? Bytes / BucketDuration.TotalSeconds : 0;

    /// <summary>
    /// True when the bucket held more distinct conversations than it could keep attribution for,
    /// so <see cref="Contributors"/> accounts for only part of <see cref="Bytes"/>.
    /// </summary>
    public bool IsTruncated { get; init; }

    public long AttributedBytes => Contributors.Sum(contributor => contributor.Bytes);
}

/// <summary>
/// One host's share of the traffic, aggregated across every flow it took part in. This is the
/// entry point for drilling down: pick a talker, then look at its own series, peers and flows.
/// </summary>
public sealed record TrafficTalker(
    string Address,
    long BytesSent,
    long BytesReceived,
    long PacketsSent,
    long PacketsReceived,
    int FlowCount,
    DateTime FirstSeen,
    DateTime LastSeen)
{
    public long TotalBytes => BytesSent + BytesReceived;

    public long TotalPackets => PacketsSent + PacketsReceived;
}

public sealed record TrafficPeer(string Address, long Bytes, long Packets);

public sealed record TrafficProtocolShare(string Protocol, long Bytes, long Packets);

public sealed record TrafficTalkerDetail(
    TrafficTalker Talker,
    TrafficWindow Window,
    IReadOnlyList<TrafficTimeBucket> TimeSeries,
    IReadOnlyList<TrafficFlow> TopFlows,
    IReadOnlyList<TrafficPeer> TopPeers,
    IReadOnlyList<TrafficProtocolShare> Protocols);

public sealed class TrafficSummary
{
    /// <summary>Totals since capture started, or since the last reset.</summary>
    public long TotalPackets { get; set; }

    public long TotalBytes { get; set; }

    public TrafficWindow Window { get; init; }

    public TimeSpan BucketDuration { get; init; }

    /// <summary>Totals within the selected window only.</summary>
    public long WindowPackets { get; set; }

    public long WindowBytes { get; set; }

    public IReadOnlyList<TrafficFlow> TopFlows { get; init; } = [];

    public IReadOnlyList<TrafficTimeBucket> TimeSeries { get; init; } = [];

    /// <summary>Current rate, measured over the last few seconds rather than the whole window.</summary>
    public double PacketsPerSecond { get; set; }

    public double BytesPerSecond { get; set; }

    /// <summary>Mean rate across the selected window.</summary>
    public double WindowAverageBytesPerSecond { get; set; }

    /// <summary>Busiest single bucket in the window, which sets the chart's vertical scale.</summary>
    public double PeakBytesPerSecond { get; set; }
}
