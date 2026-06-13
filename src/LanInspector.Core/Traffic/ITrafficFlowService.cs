namespace LanInspector.Core.Traffic;

public interface ITrafficFlowService
{
    TrafficSummary GetSummary(int topFlowsCount = 10, int buckets = 60);
    IReadOnlyList<TrafficFlow> GetFlowsForIp(string ipAddress);
    void Reset();
    event EventHandler? DataUpdated;
}
