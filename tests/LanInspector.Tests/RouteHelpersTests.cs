using System.Net;
using LanInspector.Core.Diagnostics;
using Xunit;

namespace LanInspector.Tests;

public sealed class RouteHelpersTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.87.243", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("1.2.3.4", false)]
    public void IsRfc1918_CorrectlyClassifies(string ip, bool expected)
    {
        Assert.Equal(expected, RouteHelpers.IsRfc1918(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.96.16.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsCgnatOrTailscale_CorrectlyClassifies(string ip, bool expected)
    {
        Assert.Equal(expected, RouteHelpers.IsCgnatOrTailscale(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    public void IsPublicInternet_CorrectlyClassifies(string ip, bool expected)
    {
        Assert.Equal(expected, RouteHelpers.IsPublicInternet(IPAddress.Parse(ip)));
    }

    [Fact]
    public void DetectMisconfiguration_CgnatNextHop_ReturnsEgressCgnat()
    {
        var route = new RouteDecision(
            IPAddress.Parse("192.168.87.243"),
            IPAddress.Parse("192.168.4.32"),
            IPAddress.Parse("100.96.16.1"),
            "wlan0",
            "via 100.96.16.1 on wlan0",
            ReachabilityKind.Unknown);

        var result = RouteHelpers.DetectMisconfiguration(route);

        Assert.NotNull(result);
        Assert.Equal(RouteMisconfigurationKind.EgressCgnat, result.Kind);
    }

    [Fact]
    public void DetectMisconfiguration_DirectRoute_ReturnsNull()
    {
        var route = new RouteDecision(
            IPAddress.Parse("192.168.87.243"),
            IPAddress.Parse("192.168.87.50"),
            null,
            "eth0",
            "Direct route on eth0",
            ReachabilityKind.LocalLayer2);

        var result = RouteHelpers.DetectMisconfiguration(route);

        Assert.Null(result);
    }

    [Fact]
    public void DetectMisconfiguration_PublicTarget_ReturnsNull()
    {
        var route = new RouteDecision(
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("192.168.0.100"),
            IPAddress.Parse("100.96.16.1"),
            "wlan0",
            "via 100.96.16.1 on wlan0",
            ReachabilityKind.Unknown);

        var result = RouteHelpers.DetectMisconfiguration(route);

        Assert.Null(result);
    }
}
