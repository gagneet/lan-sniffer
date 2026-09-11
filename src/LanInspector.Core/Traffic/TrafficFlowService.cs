using System.Collections.Concurrent;
using System.Net;

namespace LanInspector.Core.Traffic;

/// <summary>
/// Aggregates captured packets into flows, per-host totals, and throughput over time.
/// </summary>
/// <remarks>
/// Time series are kept at two resolutions. Second buckets give the live view its detail but are
/// only retained for a couple of minutes; minute buckets are rolled up alongside them and retained
/// for hours. Serving an hour-long chart from second buckets would mean holding 3,600 buckets per
/// host and drawing 3,600 bars, so the longer windows read the minute series instead.
/// </remarks>
public sealed class TrafficFlowService : ITrafficFlowService
{
    private const int MaxSecondBuckets = 150;
    private const int MaxMinuteBuckets = 200;

    /// <summary>
    /// Ceiling on hosts with their own retained series. A broadcast storm or a scan can otherwise
    /// mint an unbounded number of per-host series; totals for hosts beyond the cap are still
    /// counted in the flow table and the global series.
    /// </summary>
    private const int MaxTrackedTalkers = 512;

    private static readonly TimeSpan SecondBucket = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinuteBucket = TimeSpan.FromMinutes(1);

    /// <summary>Window used for the headline "right now" rate.</summary>
    private static readonly TimeSpan InstantaneousRateWindow = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, TrafficFlow> _flows = new();
    private readonly ConcurrentDictionary<string, TalkerState> _talkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSeries _seconds = new(SecondBucket, MaxSecondBuckets, trackContributors: true);
    private readonly TimeSeries _minutes = new(MinuteBucket, MaxMinuteBuckets, trackContributors: true);
    private readonly TimeProvider _timeProvider;
    private long _totalPackets;
    private long _totalBytes;

    public event EventHandler? DataUpdated;

    public TrafficFlowService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Record(IPAddress src, IPAddress dst, int srcPort, int dstPort, string protocol, int bytes)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var key = new TrafficFlowKey(src, dst, srcPort, dstPort, protocol).Normalised();
        var keyStr = $"{key.SourceIp}:{key.SourcePort}-{key.DestIp}:{key.DestPort}/{key.Protocol}";

        _flows.AddOrUpdate(
            keyStr,
            _ => new TrafficFlow { Key = key, Packets = 1, Bytes = bytes, FirstSeen = now, LastSeen = now },
            (_, flow) =>
            {
                lock (flow)
                {
                    flow.Packets++;
                    flow.Bytes += bytes;
                    flow.LastSeen = now;
                }

                return flow;
            });

        // Direction is recorded before the key is normalised, so "sent" and "received" stay
        // meaningful per host even though the flow table stores each conversation once.
        RecordTalker(src, dst, protocol, bytes, sent: true, now);
        RecordTalker(dst, src, protocol, bytes, sent: false, now);

        Interlocked.Increment(ref _totalPackets);
        Interlocked.Add(ref _totalBytes, bytes);

        var closedSecond = _seconds.Add(now, bytes, key);
        _minutes.Add(now, bytes, key);

        // Raising this per packet made the event fire thousands of times a second on a busy link
        // for a UI that only redraws once a second. Signalling on bucket close is enough.
        if (closedSecond)
        {
            DataUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RecordTalker(IPAddress address, IPAddress peer, string protocol, int bytes, bool sent, DateTime now)
    {
        var key = address.ToString();

        if (!_talkers.TryGetValue(key, out var state))
        {
            if (_talkers.Count >= MaxTrackedTalkers)
            {
                return;
            }

            state = _talkers.GetOrAdd(key, _ => new TalkerState(key, now));
        }

        state.Record(peer.ToString(), protocol, bytes, sent, now);
    }

    public TrafficSummary GetSummary(TrafficWindow window = TrafficWindow.LastMinute, int topFlowsCount = 10)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var series = GetSeriesFor(window);
        var buckets = series.Snapshot(now, window.GetBucketCount());
        var windowStart = now - window.GetDuration();

        var topFlows = _flows.Values
            .Where(flow => flow.LastSeen >= windowStart)
            .OrderByDescending(flow => flow.Bytes)
            .Take(topFlowsCount)
            .ToList();

        var windowBytes = buckets.Sum(bucket => bucket.Bytes);
        var windowPackets = buckets.Sum(bucket => bucket.Packets);
        var windowSeconds = Math.Max(window.GetDuration().TotalSeconds, 1);

        var recent = _seconds.Snapshot(now, (int)InstantaneousRateWindow.TotalSeconds);
        var recentSeconds = Math.Max(recent.Count, 1);

        return new TrafficSummary
        {
            Window = window,
            BucketDuration = window.GetBucketDuration(),
            TotalPackets = Interlocked.Read(ref _totalPackets),
            TotalBytes = Interlocked.Read(ref _totalBytes),
            WindowBytes = windowBytes,
            WindowPackets = windowPackets,
            TopFlows = topFlows,
            TimeSeries = buckets,
            PacketsPerSecond = recent.Sum(bucket => bucket.Packets) / (double)recentSeconds,
            BytesPerSecond = recent.Sum(bucket => bucket.Bytes) / (double)recentSeconds,
            WindowAverageBytesPerSecond = windowBytes / windowSeconds,
            PeakBytesPerSecond = buckets.Count > 0 ? buckets.Max(bucket => bucket.BytesPerSecond) : 0
        };
    }

    public IReadOnlyList<TrafficTalker> GetTopTalkers(TrafficWindow window, int count = 15)
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime - window.GetDuration();

        return _talkers.Values
            .Select(state => state.ToTalker(since))
            .Where(talker => talker.TotalBytes > 0)
            .OrderByDescending(talker => talker.TotalBytes)
            .Take(count)
            .ToList();
    }

    public TrafficTalkerDetail? GetTalkerDetail(string ipAddress, TrafficWindow window, int topFlowsCount = 20)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || !_talkers.TryGetValue(ipAddress, out var state))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var since = now - window.GetDuration();

        return new TrafficTalkerDetail(
            state.ToTalker(since),
            window,
            state.Snapshot(window, now),
            GetFlowsForIp(ipAddress).Where(flow => flow.LastSeen >= since).Take(topFlowsCount).ToList(),
            state.TopPeers(10),
            state.ProtocolShares());
    }

    /// <summary>
    /// What made up one bar on the chart: the conversations active during that bucket, heaviest
    /// first. Returns null for a bucket that has fallen out of the retained window.
    /// </summary>
    public TrafficBucketDetail? GetBucketDetail(DateTime bucketStart, TrafficWindow window, int topCount = 15) =>
        GetSeriesFor(window).GetDetail(bucketStart, topCount);

    public IReadOnlyList<TrafficFlow> GetFlowsForIp(string ipAddress) =>
        _flows.Values
            .Where(flow => flow.Key.SourceIp.ToString() == ipAddress || flow.Key.DestIp.ToString() == ipAddress)
            .OrderByDescending(flow => flow.Bytes)
            .ToList();

    public void Reset()
    {
        _flows.Clear();
        _talkers.Clear();
        _seconds.Reset();
        _minutes.Reset();
        Interlocked.Exchange(ref _totalPackets, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
        DataUpdated?.Invoke(this, EventArgs.Empty);
    }

    private TimeSeries GetSeriesFor(TrafficWindow window) =>
        window == TrafficWindow.LastMinute ? _seconds : _minutes;

    /// <summary>
    /// A fixed-resolution ring of time buckets. All mutation happens under one lock: the previous
    /// implementation replaced the current bucket outside the lock during a reset, which could
    /// drop or double-count a concurrent packet.
    /// </summary>
    private sealed class TimeSeries(TimeSpan bucketDuration, int maxBuckets, bool trackContributors = false)
    {
        private readonly Queue<TrafficTimeBucket> _closed = new();
        private readonly Dictionary<DateTime, BucketAttribution> _attribution = [];
        private readonly object _gate = new();
        private TrafficTimeBucket? _current;

        /// <summary>Returns true when this packet closed the previous bucket.</summary>
        public bool Add(DateTime timestamp, int bytes, TrafficFlowKey? flow = null)
        {
            var start = Floor(timestamp);
            lock (_gate)
            {
                var closed = false;

                if (_current is null)
                {
                    _current = NewBucket(start);
                }
                else if (_current.BucketStart != start)
                {
                    _closed.Enqueue(_current);
                    while (_closed.Count > maxBuckets)
                    {
                        var evicted = _closed.Dequeue();
                        _attribution.Remove(evicted.BucketStart);
                    }

                    _current = NewBucket(start);
                    closed = true;
                }

                _current.Packets++;
                _current.Bytes += bytes;

                if (trackContributors && flow is not null)
                {
                    if (!_attribution.TryGetValue(start, out var attribution))
                    {
                        attribution = new BucketAttribution();
                        _attribution[start] = attribution;
                    }

                    attribution.Add(flow, bytes);
                }

                return closed;
            }
        }

        /// <summary>
        /// What made up one bucket. Returns null for a bucket outside the retained window, or for
        /// a series that does not track attribution.
        /// </summary>
        public TrafficBucketDetail? GetDetail(DateTime bucketStart, int topCount)
        {
            var start = Floor(bucketStart);

            lock (_gate)
            {
                var bucket = _current?.BucketStart == start
                    ? _current
                    : _closed.FirstOrDefault(candidate => candidate.BucketStart == start);

                if (bucket is null)
                {
                    return null;
                }

                var attribution = _attribution.GetValueOrDefault(start);

                return new TrafficBucketDetail(
                    start,
                    bucketDuration,
                    bucket.Bytes,
                    bucket.Packets,
                    attribution?.Top(topCount) ?? [])
                {
                    IsTruncated = attribution?.IsTruncated ?? false
                };
            }
        }

        /// <summary>
        /// The most recent <paramref name="count"/> buckets ending at <paramref name="now"/>, with
        /// zero-filled gaps. Without the zero fill, a quiet period would compress the chart instead
        /// of showing the silence, and bars would not line up with wall-clock time.
        /// </summary>
        public IReadOnlyList<TrafficTimeBucket> Snapshot(DateTime now, int count)
        {
            Dictionary<DateTime, TrafficTimeBucket> byStart;
            lock (_gate)
            {
                byStart = _closed.ToDictionary(bucket => bucket.BucketStart);
                if (_current is not null)
                {
                    byStart[_current.BucketStart] = _current;
                }
            }

            var latest = Floor(now);
            var result = new List<TrafficTimeBucket>(count);

            for (var index = count - 1; index >= 0; index--)
            {
                var start = latest - bucketDuration * index;
                result.Add(byStart.TryGetValue(start, out var bucket)
                    ? new TrafficTimeBucket { BucketStart = start, BucketDuration = bucketDuration, Packets = bucket.Packets, Bytes = bucket.Bytes }
                    : NewBucket(start));
            }

            return result;
        }

        public void Reset()
        {
            lock (_gate)
            {
                _closed.Clear();
                _attribution.Clear();
                _current = null;
            }
        }

        private DateTime Floor(DateTime timestamp) =>
            new(timestamp.Ticks - timestamp.Ticks % bucketDuration.Ticks, timestamp.Kind);

        private TrafficTimeBucket NewBucket(DateTime start) =>
            new() { BucketStart = start, BucketDuration = bucketDuration };
    }

    /// <summary>
    /// Per-conversation byte and packet counts inside one time bucket.
    /// </summary>
    /// <remarks>
    /// Capped, because attribution is kept for every retained bucket at both resolutions and a
    /// scan or a broadcast storm can mint conversations without limit. Once the cap is reached
    /// existing conversations keep counting but new ones are dropped, and the bucket is flagged
    /// truncated so the UI can say the breakdown is partial rather than quietly under-reporting.
    /// </remarks>
    private sealed class BucketAttribution
    {
        private const int MaxContributors = 64;

        private readonly Dictionary<TrafficFlowKey, long[]> _contributors = [];

        public bool IsTruncated { get; private set; }

        public void Add(TrafficFlowKey flow, int bytes)
        {
            if (!_contributors.TryGetValue(flow, out var counters))
            {
                if (_contributors.Count >= MaxContributors)
                {
                    IsTruncated = true;
                    return;
                }

                counters = new long[2];
                _contributors[flow] = counters;
            }

            counters[0] += bytes;
            counters[1]++;
        }

        public IReadOnlyList<TrafficBucketContributor> Top(int count) =>
            _contributors
                .OrderByDescending(entry => entry.Value[0])
                .Take(count)
                .Select(entry => new TrafficBucketContributor(
                    $"{entry.Key.SourceIp}:{entry.Key.SourcePort}",
                    $"{entry.Key.DestIp}:{entry.Key.DestPort}",
                    entry.Key.Protocol,
                    entry.Value[0],
                    entry.Value[1]))
                .ToList();
    }

    private sealed class TalkerState(string address, DateTime firstSeen)
    {
        private readonly TimeSeries _minutes = new(MinuteBucket, MaxMinuteBuckets);
        private readonly TimeSeries _seconds = new(SecondBucket, MaxSecondBuckets);
        private readonly ConcurrentDictionary<string, long[]> _peers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long[]> _protocols = new(StringComparer.OrdinalIgnoreCase);
        private long _bytesSent;
        private long _bytesReceived;
        private long _packetsSent;
        private long _packetsReceived;
        private long _lastSeenTicks = firstSeen.Ticks;

        public void Record(string peer, string protocol, int bytes, bool sent, DateTime now)
        {
            if (sent)
            {
                Interlocked.Add(ref _bytesSent, bytes);
                Interlocked.Increment(ref _packetsSent);
            }
            else
            {
                Interlocked.Add(ref _bytesReceived, bytes);
                Interlocked.Increment(ref _packetsReceived);
            }

            Interlocked.Exchange(ref _lastSeenTicks, now.Ticks);

            Accumulate(_peers, peer, bytes);
            Accumulate(_protocols, protocol, bytes);

            _seconds.Add(now, bytes);
            _minutes.Add(now, bytes);
        }

        public TrafficTalker ToTalker(DateTime since)
        {
            var lastSeen = new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);

            // Totals are lifetime figures; a host that has gone quiet inside the window reports
            // zero so that it drops out of the ranking rather than lingering at its old volume.
            var isActive = lastSeen >= since;

            return new TrafficTalker(
                address,
                isActive ? Interlocked.Read(ref _bytesSent) : 0,
                isActive ? Interlocked.Read(ref _bytesReceived) : 0,
                isActive ? Interlocked.Read(ref _packetsSent) : 0,
                isActive ? Interlocked.Read(ref _packetsReceived) : 0,
                _peers.Count,
                firstSeen,
                lastSeen);
        }

        public IReadOnlyList<TrafficTimeBucket> Snapshot(TrafficWindow window, DateTime now) =>
            (window == TrafficWindow.LastMinute ? _seconds : _minutes).Snapshot(now, window.GetBucketCount());

        public IReadOnlyList<TrafficPeer> TopPeers(int count) =>
            _peers
                .Select(entry => new TrafficPeer(entry.Key, Interlocked.Read(ref entry.Value[0]), Interlocked.Read(ref entry.Value[1])))
                .OrderByDescending(peer => peer.Bytes)
                .Take(count)
                .ToList();

        public IReadOnlyList<TrafficProtocolShare> ProtocolShares() =>
            _protocols
                .Select(entry => new TrafficProtocolShare(entry.Key, Interlocked.Read(ref entry.Value[0]), Interlocked.Read(ref entry.Value[1])))
                .OrderByDescending(share => share.Bytes)
                .ToList();

        private static void Accumulate(ConcurrentDictionary<string, long[]> counters, string key, int bytes)
        {
            var counter = counters.GetOrAdd(key, _ => new long[2]);
            Interlocked.Add(ref counter[0], bytes);
            Interlocked.Increment(ref counter[1]);
        }
    }
}
