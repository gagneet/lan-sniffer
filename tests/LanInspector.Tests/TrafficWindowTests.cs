using System.Net;
using LanInspector.Core.Traffic;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// Exercises the hour-long view and the per-host drill-down. A fake clock is used so an hour of
/// history can be built without an hour of waiting.
/// </summary>
public sealed class TrafficWindowTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private static readonly IPAddress Server = IPAddress.Parse("192.168.0.154");
    private static readonly IPAddress Laptop = IPAddress.Parse("192.168.0.50");
    private static readonly IPAddress Nas = IPAddress.Parse("192.168.0.60");

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void GetSummary_LastHour_ReturnsSixtyMinuteBuckets()
    {
        var summary = new TrafficFlowService(NewClock()).GetSummary(TrafficWindow.LastHour);

        Assert.Equal(60, summary.TimeSeries.Count);
        Assert.All(summary.TimeSeries, bucket => Assert.Equal(TimeSpan.FromMinutes(1), bucket.BucketDuration));
        Assert.Equal(TimeSpan.FromMinutes(1), summary.BucketDuration);
    }

    [Fact]
    public void GetSummary_LastMinute_ReturnsSixtySecondBuckets()
    {
        var summary = new TrafficFlowService(NewClock()).GetSummary(TrafficWindow.LastMinute);

        Assert.Equal(60, summary.TimeSeries.Count);
        Assert.All(summary.TimeSeries, bucket => Assert.Equal(TimeSpan.FromSeconds(1), bucket.BucketDuration));
    }

    [Fact]
    public void GetSummary_TrafficSpreadOverAnHour_IsAllVisibleInTheHourWindow()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        // One packet a minute for 50 minutes.
        for (var minute = 0; minute < 50; minute++)
        {
            service.Record(Server, Laptop, 22, 51000, "TCP", 1000);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var hour = service.GetSummary(TrafficWindow.LastHour);

        Assert.Equal(50_000, hour.WindowBytes);
        Assert.Equal(50, hour.WindowPackets);
        Assert.Equal(50, hour.TimeSeries.Count(bucket => bucket.Bytes > 0));
    }

    [Fact]
    public void GetSummary_OldTraffic_FallsOutOfTheShorterWindowButStaysInTheLongerOne()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 5000);
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(0, service.GetSummary(TrafficWindow.LastMinute).WindowBytes);
        Assert.Equal(0, service.GetSummary(TrafficWindow.LastFifteenMinutes).WindowBytes);
        Assert.Equal(5000, service.GetSummary(TrafficWindow.LastHour).WindowBytes);
    }

    [Fact]
    public void GetSummary_QuietPeriods_AreZeroFilledRatherThanCompressedAway()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 1000);
        clock.Advance(TimeSpan.FromMinutes(10));
        service.Record(Server, Laptop, 22, 51000, "TCP", 1000);

        var series = service.GetSummary(TrafficWindow.LastHour).TimeSeries;

        Assert.Equal(60, series.Count);
        Assert.Equal(2, series.Count(bucket => bucket.Bytes > 0));
        // Buckets are contiguous and ordered oldest first, so bars line up with wall-clock time.
        for (var index = 1; index < series.Count; index++)
        {
            Assert.Equal(series[index - 1].BucketStart.AddMinutes(1), series[index].BucketStart);
        }
    }

    [Fact]
    public void GetSummary_WindowAverageAndPeak_AreComputedOverTheWindow()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 60_000);

        var hour = service.GetSummary(TrafficWindow.LastHour);

        // 60 KB across a 3,600-second window.
        Assert.Equal(60_000d / 3600, hour.WindowAverageBytesPerSecond, 3);
        // ...but all of it landed in one 60-second bucket.
        Assert.Equal(1000, hour.PeakBytesPerSecond, 3);
    }

    [Fact]
    public void GetTopTalkers_RanksHostsByVolumeAndSplitsDirection()
    {
        var service = new TrafficFlowService(NewClock());

        service.Record(Server, Laptop, 22, 51000, "TCP", 10_000);
        service.Record(Laptop, Server, 51000, 22, "TCP", 500);
        service.Record(Nas, Laptop, 445, 52000, "TCP", 2_000);

        var talkers = service.GetTopTalkers(TrafficWindow.LastHour);

        // The laptop is on both conversations, so it moved the most overall.
        Assert.Equal("192.168.0.50", talkers[0].Address);
        Assert.Equal(500, talkers[0].BytesSent);
        Assert.Equal(12_000, talkers[0].BytesReceived);

        var server = talkers.Single(talker => talker.Address == "192.168.0.154");
        Assert.Equal(10_000, server.BytesSent);
        Assert.Equal(500, server.BytesReceived);
        Assert.Equal(10_500, server.TotalBytes);

        var nas = talkers.Single(talker => talker.Address == "192.168.0.60");
        Assert.Equal(2_000, nas.BytesSent);
        Assert.Equal(0, nas.BytesReceived);
    }

    [Fact]
    public void GetTopTalkers_HostSilentThroughoutTheWindow_IsExcluded()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 10_000);
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(service.GetTopTalkers(TrafficWindow.LastFifteenMinutes));
        Assert.NotEmpty(service.GetTopTalkers(TrafficWindow.LastHour));
    }

    [Fact]
    public void GetTalkerDetail_ReturnsOwnSeriesPeersProtocolsAndFlows()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 10_000);
        clock.Advance(TimeSpan.FromMinutes(5));
        service.Record(Server, Nas, 2049, 53000, "UDP", 4_000);

        var detail = service.GetTalkerDetail("192.168.0.154", TrafficWindow.LastHour);

        Assert.NotNull(detail);
        Assert.Equal(14_000, detail!.Talker.BytesSent);
        Assert.Equal(60, detail.TimeSeries.Count);
        Assert.Equal(2, detail.TimeSeries.Count(bucket => bucket.Bytes > 0));
        Assert.Equal(["192.168.0.50", "192.168.0.60"], detail.TopPeers.Select(peer => peer.Address).Order());
        Assert.Equal(["TCP", "UDP"], detail.Protocols.Select(share => share.Protocol).Order());
        Assert.Equal(2, detail.TopFlows.Count);
    }

    [Fact]
    public void GetTalkerDetail_HostNeverSeen_ReturnsNull()
    {
        var service = new TrafficFlowService(NewClock());
        service.Record(Server, Laptop, 22, 51000, "TCP", 1000);

        Assert.Null(service.GetTalkerDetail("10.1.2.3", TrafficWindow.LastHour));
        Assert.Null(service.GetTalkerDetail("", TrafficWindow.LastHour));
    }

    [Fact]
    public void GetSummary_InstantaneousRate_IgnoresTheSelectedWindow()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 10_000);
        clock.Advance(TimeSpan.FromMinutes(20));

        // The traffic is inside the hour window but outside the 10-second rate window.
        var hour = service.GetSummary(TrafficWindow.LastHour);
        Assert.Equal(10_000, hour.WindowBytes);
        Assert.Equal(0, hour.BytesPerSecond);
    }

    [Fact]
    public void Reset_ClearsTotalsTalkersAndSeries()
    {
        var service = new TrafficFlowService(NewClock());
        service.Record(Server, Laptop, 22, 51000, "TCP", 10_000);

        service.Reset();

        var summary = service.GetSummary(TrafficWindow.LastHour);
        Assert.Equal(0, summary.TotalBytes);
        Assert.Equal(0, summary.WindowBytes);
        Assert.Empty(summary.TopFlows);
        Assert.Empty(service.GetTopTalkers(TrafficWindow.LastHour));
        Assert.Null(service.GetTalkerDetail("192.168.0.154", TrafficWindow.LastHour));
    }

    [Fact]
    public void DataUpdated_FiresOnBucketRolloverNotOnEveryPacket()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);
        var fired = 0;
        service.DataUpdated += (_, _) => fired++;

        for (var i = 0; i < 100; i++)
        {
            service.Record(Server, Laptop, 22, 51000, "TCP", 100);
        }

        Assert.Equal(0, fired);

        clock.Advance(TimeSpan.FromSeconds(1));
        service.Record(Server, Laptop, 22, 51000, "TCP", 100);

        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData(TrafficWindow.LastMinute, 60)]
    [InlineData(TrafficWindow.LastFifteenMinutes, 15)]
    [InlineData(TrafficWindow.LastHour, 60)]
    [InlineData(TrafficWindow.LastThreeHours, 180)]
    public void WindowMetadata_MatchesItsLabel(TrafficWindow window, int expectedBuckets)
    {
        Assert.Equal(expectedBuckets, window.GetBucketCount());
        Assert.Equal(window.GetBucketDuration() * expectedBuckets, window.GetDuration());
        Assert.NotEmpty(window.GetLabel());
    }

    [Fact]
    public void WindowDurations_MatchTheirNames()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), TrafficWindow.LastMinute.GetDuration());
        Assert.Equal(TimeSpan.FromMinutes(15), TrafficWindow.LastFifteenMinutes.GetDuration());
        Assert.Equal(TimeSpan.FromHours(1), TrafficWindow.LastHour.GetDuration());
        Assert.Equal(TimeSpan.FromHours(3), TrafficWindow.LastThreeHours.GetDuration());
    }
}
