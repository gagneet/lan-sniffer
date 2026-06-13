using System.Net;
using LanInspector.Core.Traffic;
using Xunit;

namespace LanInspector.Tests;

public sealed class TrafficFlowServiceTests
{
    [Fact]
    public void Record_SinglePacket_IncrementsTotalPackets()
    {
        var svc = new TrafficFlowService();
        svc.Record(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.2"), 12345, 80, "TCP", 500);

        var summary = svc.GetSummary();
        Assert.Equal(1, summary.TotalPackets);
    }

    [Fact]
    public void Record_SinglePacket_IncrementsTotalBytes()
    {
        var svc = new TrafficFlowService();
        svc.Record(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.2"), 12345, 80, "TCP", 500);

        var summary = svc.GetSummary();
        Assert.Equal(500, summary.TotalBytes);
    }

    [Fact]
    public void Record_MultiplePackets_AccumulatesBytes()
    {
        var svc = new TrafficFlowService();
        svc.Record(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"), 100, 443, "TCP", 1000);
        svc.Record(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"), 100, 443, "TCP", 2000);

        var summary = svc.GetSummary();
        Assert.Equal(3000, summary.TotalBytes);
        Assert.Equal(2, summary.TotalPackets);
    }

    [Fact]
    public void Record_SameFlowBothDirections_MergesIntoOneFlow()
    {
        var svc = new TrafficFlowService();
        var a = IPAddress.Parse("192.168.1.1");
        var b = IPAddress.Parse("192.168.1.2");

        svc.Record(a, b, 5000, 80, "TCP", 100);
        svc.Record(b, a, 80, 5000, "TCP", 200);

        var summary = svc.GetSummary();
        Assert.Single(summary.TopFlows);
        Assert.Equal(300, summary.TopFlows[0].Bytes);
    }

    [Fact]
    public void GetFlowsForIp_ReturnsOnlyMatchingFlows()
    {
        var svc = new TrafficFlowService();
        svc.Record(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2"), 100, 80, "TCP", 100);
        svc.Record(IPAddress.Parse("10.0.0.3"), IPAddress.Parse("10.0.0.4"), 200, 443, "TCP", 200);

        var flows = svc.GetFlowsForIp("10.0.0.1");
        Assert.Single(flows);
    }

    [Fact]
    public void Reset_ClearsAllData()
    {
        var svc = new TrafficFlowService();
        svc.Record(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("5.6.7.8"), 100, 80, "TCP", 500);
        svc.Reset();

        var summary = svc.GetSummary();
        Assert.Equal(0, summary.TotalPackets);
        Assert.Equal(0, summary.TotalBytes);
        Assert.Empty(summary.TopFlows);
    }

    [Fact]
    public void GetSummary_TopFlows_LimitedByCount()
    {
        var svc = new TrafficFlowService();
        for (var i = 0; i < 20; i++)
        {
            svc.Record(
                IPAddress.Parse($"10.0.0.{i + 1}"),
                IPAddress.Parse("10.0.0.100"),
                i + 1000, 80, "TCP", 100);
        }

        var summary = svc.GetSummary(topFlowsCount: 5);
        Assert.Equal(5, summary.TopFlows.Count);
    }

    [Fact]
    public void DataUpdated_FiredOnRecord()
    {
        var svc = new TrafficFlowService();
        var fired = false;
        svc.DataUpdated += (_, _) => fired = true;

        svc.Record(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2.2.2.2"), 100, 80, "TCP", 100);

        Assert.True(fired);
    }
}
