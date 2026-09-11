using System.Net;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Network;
using Xunit;

namespace LanInspector.Tests;

public sealed class ReachabilityExplainerTests
{
    private static LocalNetworkProfile ProfileOn(string address, string gateway) => new(
    [
        new LocalNetworkInterface(
            "eth0",
            "Test adapter",
            IPAddress.Parse(address),
            IPv4Network.FromAddressAndPrefix(IPAddress.Parse(address), 24),
            IPAddress.Parse(gateway))
    ]);

    [Fact]
    public void Explain_ServiceAnswered_SaysNothing()
    {
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.0.50", "192.168.0.1"),
            route: null,
            serviceAnswered: true);

        Assert.Equal(string.Empty, explanation);
    }

    [Fact]
    public void Explain_TargetOnADifferentSubnet_BlamesTheRouteNotTheDevice()
    {
        // This is the shape of the real failure: the application runs on the 192.168.87.x side and
        // the server sits behind another router on 192.168.0.x.
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false);

        Assert.Contains("192.168.87.0/24", explanation);
        Assert.Contains("192.168.0.0/24", explanation);
        Assert.Contains("different subnets", explanation);
        Assert.DoesNotContain("firewall", explanation);
    }

    [Fact]
    public void Explain_TargetOnTheSameSubnet_BlamesTheServiceNotTheRoute()
    {
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.0.50", "192.168.0.1"),
            route: null,
            serviceAnswered: false);

        Assert.Contains("own subnet", explanation);
        Assert.Contains("firewall", explanation);
        Assert.DoesNotContain("different subnets", explanation);
    }

    [Fact]
    public void Explain_RouteExitsViaCgnat_UsesTheRouteDiagnosis()
    {
        var route = new RouteDecision(
            IPAddress.Parse("192.168.0.148"),
            IPAddress.Parse("192.168.87.20"),
            IPAddress.Parse("100.96.0.1"),
            "eth0",
            "via 100.96.0.1",
            ReachabilityKind.Routed);

        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route,
            serviceAnswered: false);

        Assert.Contains("does not know how to reach", explanation);
    }

    [Fact]
    public void Explain_TailscaleAvailable_OffersItAsTheWayIn()
    {
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false,
            tailscaleAddress: IPAddress.Parse("100.83.183.74"),
            tailscaleName: "ubuntu-svr");

        Assert.Contains("Tailscale reaches it now at ubuntu-svr", explanation);
    }

    [Fact]
    public void Explain_TailscaleWithoutAName_FallsBackToTheAddress()
    {
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false,
            tailscaleAddress: IPAddress.Parse("100.83.183.74"));

        Assert.Contains("100.83.183.74", explanation);
    }

    [Fact]
    public void Explain_NoLocalInterfaces_StillProducesAnExplanation()
    {
        var explanation = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            new LocalNetworkProfile([]),
            route: null,
            serviceAnswered: false);

        Assert.Contains("no active IPv4 interface", explanation);
    }
}
