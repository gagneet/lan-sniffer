namespace LanInspector.Core.Traffic;

public interface ITrafficFlowService
{
    TrafficSummary GetSummary(TrafficWindow window = TrafficWindow.LastMinute, int topFlowsCount = 10);

    /// <summary>Hosts ranked by the volume they moved inside the window.</summary>
    IReadOnlyList<TrafficTalker> GetTopTalkers(TrafficWindow window, int count = 15);

    /// <summary>
    /// Everything known about one host inside the window: its own throughput series, the peers it
    /// talked to, the protocols it used, and its heaviest flows. Null if the host was not seen.
    /// </summary>
    TrafficTalkerDetail? GetTalkerDetail(string ipAddress, TrafficWindow window, int topFlowsCount = 20);

    /// <summary>
    /// The conversations that made up one time bucket — what a click on a chart bar shows.
    /// Null when the bucket is outside the retained window.
    /// </summary>
    TrafficBucketDetail? GetBucketDetail(DateTime bucketStart, TrafficWindow window, int topCount = 15);

    IReadOnlyList<TrafficFlow> GetFlowsForIp(string ipAddress);

    void Reset();

    /// <summary>
    /// Raised when a time bucket closes — roughly once a second under load, not once per packet.
    /// </summary>
    event EventHandler? DataUpdated;
}
