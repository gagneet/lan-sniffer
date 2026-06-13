using System.Collections.Concurrent;

namespace LanInspector.Core.Traffic;

public sealed class TrafficFlowService : ITrafficFlowService
{
    private readonly ConcurrentDictionary<string, TrafficFlow> _flows = new();
    private readonly Queue<TrafficTimeBucket> _timeSeries = new();
    private readonly TimeSpan _bucketDuration = TimeSpan.FromSeconds(1);
    private readonly int _maxBuckets = 120;
    private TrafficTimeBucket _currentBucket;
    private long _totalPackets;
    private long _totalBytes;

    public event EventHandler? DataUpdated;

    public TrafficFlowService()
    {
        _currentBucket = NewBucket(DateTime.UtcNow);
    }

    public void Record(System.Net.IPAddress src, System.Net.IPAddress dst, int srcPort, int dstPort, string protocol, int bytes)
    {
        var key = new TrafficFlowKey(src, dst, srcPort, dstPort, protocol).Normalised();
        var keyStr = $"{key.SourceIp}:{key.SourcePort}-{key.DestIp}:{key.DestPort}/{key.Protocol}";

        _flows.AddOrUpdate(keyStr,
            _ => new TrafficFlow { Key = key, Packets = 1, Bytes = bytes },
            (_, flow) => { lock (flow) { flow.Packets++; flow.Bytes += bytes; flow.LastSeen = DateTime.UtcNow; } return flow; });

        Interlocked.Increment(ref _totalPackets);
        Interlocked.Add(ref _totalBytes, bytes);

        AdvanceBucket(bytes);
        DataUpdated?.Invoke(this, EventArgs.Empty);
    }

    public TrafficSummary GetSummary(int topFlowsCount = 10, int buckets = 60)
    {
        var allFlows = _flows.Values.OrderByDescending(f => f.Bytes).ToList();
        var topFlows = allFlows.Take(topFlowsCount).ToList();

        List<TrafficTimeBucket> series;
        lock (_timeSeries)
        {
            series = _timeSeries.TakeLast(buckets).ToList();
        }

        var elapsed = series.Count > 0
            ? (series.Last().BucketStart - series.First().BucketStart + _bucketDuration).TotalSeconds
            : 1;

        return new TrafficSummary
        {
            TotalPackets = _totalPackets,
            TotalBytes = _totalBytes,
            TopFlows = topFlows,
            TimeSeries = series,
            PacketsPerSecond = series.Sum(b => b.Packets) / Math.Max(elapsed, 1),
            BytesPerSecond = series.Sum(b => b.Bytes) / Math.Max(elapsed, 1)
        };
    }

    public IReadOnlyList<TrafficFlow> GetFlowsForIp(string ipAddress) =>
        _flows.Values
            .Where(f => f.Key.SourceIp.ToString() == ipAddress || f.Key.DestIp.ToString() == ipAddress)
            .OrderByDescending(f => f.Bytes)
            .ToList();

    public void Reset()
    {
        _flows.Clear();
        lock (_timeSeries) { _timeSeries.Clear(); }
        _currentBucket = NewBucket(DateTime.UtcNow);
        Interlocked.Exchange(ref _totalPackets, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
    }

    private void AdvanceBucket(int bytes)
    {
        var now = DateTime.UtcNow;
        lock (_timeSeries)
        {
            if (now - _currentBucket.BucketStart >= _bucketDuration)
            {
                _timeSeries.Enqueue(_currentBucket);
                while (_timeSeries.Count > _maxBuckets)
                    _timeSeries.Dequeue();
                _currentBucket = NewBucket(now);
            }

            _currentBucket.Packets++;
            _currentBucket.Bytes += bytes;
        }
    }

    private TrafficTimeBucket NewBucket(DateTime start) =>
        new() { BucketStart = start, BucketDuration = _bucketDuration };
}
