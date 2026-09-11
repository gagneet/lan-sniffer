using System.Net;
using LanInspector.Core.Traffic;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// Covers "what was using the network at that moment?" — the per-bucket attribution behind a
/// click on a chart bar.
/// </summary>
public sealed class TrafficBucketDetailTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private static readonly IPAddress Server = IPAddress.Parse("192.168.0.148");
    private static readonly IPAddress Laptop = IPAddress.Parse("192.168.0.50");
    private static readonly IPAddress Nas = IPAddress.Parse("192.168.0.60");

    private static readonly DateTimeOffset Start = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private static FakeClock NewClock() => new(Start);

    [Fact]
    public void GetBucketDetail_ReturnsTheConversationsActiveInThatBucket()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 8_000);
        service.Record(Nas, Laptop, 445, 52000, "TCP", 2_000);

        var detail = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour);

        Assert.NotNull(detail);
        Assert.Equal(10_000, detail!.Bytes);
        Assert.Equal(2, detail.Packets);
        Assert.Equal(2, detail.Contributors.Count);

        // Heaviest conversation first.
        Assert.Equal("TCP", detail.Contributors[0].Protocol);
        Assert.Equal(8_000, detail.Contributors[0].Bytes);
        Assert.Contains("192.168.0.148", $"{detail.Contributors[0].Source}{detail.Contributors[0].Destination}");
    }

    [Fact]
    public void GetBucketDetail_AttributesEachBucketSeparately()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 5_000);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Record(Nas, Laptop, 445, 52000, "TCP", 3_000);

        var first = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour);
        var second = service.GetBucketDetail(Start.AddMinutes(1).UtcDateTime, TrafficWindow.LastHour);

        Assert.Equal(5_000, first!.Bytes);
        Assert.Equal(3_000, second!.Bytes);

        // Flow keys are normalised so a conversation is stored once regardless of which side sent
        // first, which means the NAS can appear as either endpoint.
        var conversation = Assert.Single(second.Contributors);
        Assert.Contains("192.168.0.60:445", $"{conversation.Source} {conversation.Destination}");
        Assert.Contains("192.168.0.50:52000", $"{conversation.Source} {conversation.Destination}");
    }

    [Fact]
    public void GetBucketDetail_RepeatedPacketsOnOneFlow_AreAccumulated()
    {
        var service = new TrafficFlowService(NewClock());

        for (var i = 0; i < 5; i++)
        {
            service.Record(Server, Laptop, 22, 51000, "TCP", 1_000);
        }

        var detail = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour);

        var contributor = Assert.Single(detail!.Contributors);
        Assert.Equal(5_000, contributor.Bytes);
        Assert.Equal(5, contributor.Packets);
    }

    [Fact]
    public void GetBucketDetail_SecondResolution_IsServedFromTheLiveSeries()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 4_000);
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Record(Nas, Laptop, 445, 52000, "TCP", 1_000);

        var first = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastMinute);
        var second = service.GetBucketDetail(Start.AddSeconds(1).UtcDateTime, TrafficWindow.LastMinute);

        Assert.Equal(4_000, first!.Bytes);
        Assert.Equal(TimeSpan.FromSeconds(1), first.BucketDuration);
        Assert.Equal(1_000, second!.Bytes);
    }

    [Fact]
    public void GetBucketDetail_BucketOutsideTheRetainedWindow_ReturnsNull()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 1_000);

        Assert.Null(service.GetBucketDetail(Start.AddHours(-5).UtcDateTime, TrafficWindow.LastHour));
        Assert.Null(service.GetBucketDetail(Start.AddHours(1).UtcDateTime, TrafficWindow.LastHour));
    }

    [Fact]
    public void GetBucketDetail_MoreConversationsThanTheCap_IsFlaggedTruncated()
    {
        var service = new TrafficFlowService(NewClock());

        // The cap is 64 conversations per bucket.
        for (var port = 0; port < 100; port++)
        {
            service.Record(Laptop, IPAddress.Parse($"10.1.{port / 256}.{port % 256}"), 50000 + port, 443, "TCP", 100);
        }

        var detail = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour);

        Assert.True(detail!.IsTruncated);
        // The bucket's own totals stay complete even though attribution is partial.
        Assert.Equal(10_000, detail.Bytes);
        Assert.True(detail.AttributedBytes < detail.Bytes);
    }

    [Fact]
    public void GetBucketDetail_TopCount_LimitsTheRowsWithoutFlaggingTruncation()
    {
        var service = new TrafficFlowService(NewClock());

        for (var port = 0; port < 10; port++)
        {
            service.Record(Laptop, Server, 50000 + port, 443, "TCP", 100 * (port + 1));
        }

        var detail = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour, topCount: 3);

        Assert.Equal(3, detail!.Contributors.Count);
        Assert.False(detail.IsTruncated);
        // Ranked by volume, heaviest first.
        Assert.Equal(1_000, detail.Contributors[0].Bytes);
        Assert.Equal(900, detail.Contributors[1].Bytes);
    }

    [Fact]
    public void GetBucketDetail_AfterReset_ReturnsNull()
    {
        var service = new TrafficFlowService(NewClock());
        service.Record(Server, Laptop, 22, 51000, "TCP", 1_000);

        service.Reset();

        Assert.Null(service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour));
    }

    [Fact]
    public void GetBucketDetail_BytesPerSecond_UsesTheBucketDuration()
    {
        var service = new TrafficFlowService(NewClock());
        service.Record(Server, Laptop, 22, 51000, "TCP", 60_000);

        var minute = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour);
        var second = service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastMinute);

        Assert.Equal(1_000, minute!.BytesPerSecond, 3);
        Assert.Equal(60_000, second!.BytesPerSecond, 3);
    }

    [Fact]
    public void GetBucketDetail_EvictedBucket_DropsItsAttributionToo()
    {
        var clock = NewClock();
        var service = new TrafficFlowService(clock);

        service.Record(Server, Laptop, 22, 51000, "TCP", 1_000);

        // The minute ring retains 200 buckets; walk well past that.
        for (var minute = 0; minute < 205; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            service.Record(Nas, Laptop, 445, 52000, "TCP", 10);
        }

        Assert.Null(service.GetBucketDetail(Start.UtcDateTime, TrafficWindow.LastHour));
    }
}
