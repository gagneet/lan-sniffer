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
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.0.50", "192.168.0.1"),
            route: null,
            serviceAnswered: true);

        Assert.False(diagnosis.HasDiagnosis);
        Assert.Equal(ReachabilityCause.None, diagnosis.Cause);
        Assert.Equal(string.Empty, diagnosis.ShortCause);
        Assert.Equal(string.Empty, diagnosis.Detail);
        Assert.Equal(string.Empty, diagnosis.ToSingleLine());
    }

    [Fact]
    public void Explain_TargetOnADifferentSubnet_BlamesTheRouteNotTheDevice()
    {
        // This is the shape of the real failure: the application runs on the 192.168.87.x side and
        // the server sits behind another router on 192.168.0.x.
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false);

        Assert.Equal(ReachabilityCause.DifferentSubnet, diagnosis.Cause);
        Assert.Contains("192.168.87.0/24", diagnosis.Detail);
        Assert.Contains("192.168.0.0/24", diagnosis.Detail);
        Assert.Contains("different subnets", diagnosis.Detail);
        Assert.DoesNotContain("firewall", diagnosis.Detail);
    }

    [Fact]
    public void Explain_TargetOnTheSameSubnet_BlamesTheServiceNotTheRoute()
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.0.50", "192.168.0.1"),
            route: null,
            serviceAnswered: false);

        Assert.Equal(ReachabilityCause.ServiceNotAnswering, diagnosis.Cause);
        Assert.Contains("own subnet", diagnosis.Detail);
        Assert.Contains("firewall", diagnosis.Remedy);
        Assert.DoesNotContain("different subnets", diagnosis.Detail);
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

        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route,
            serviceAnswered: false);

        Assert.Equal(ReachabilityCause.RoutedUpstream, diagnosis.Cause);
        Assert.Contains("does not know how to reach", diagnosis.Detail);
    }

    [Fact]
    public void Explain_TailscaleAvailable_OffersItAsTheWayIn()
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false,
            tailscaleAddress: IPAddress.Parse("100.83.183.74"),
            tailscaleName: "ubuntu-svr");

        Assert.Contains("Tailscale reaches it now at ubuntu-svr", diagnosis.Detail);
    }

    [Fact]
    public void Explain_TailscaleWithoutAName_FallsBackToTheAddress()
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false,
            tailscaleAddress: IPAddress.Parse("100.83.183.74"));

        Assert.Contains("100.83.183.74", diagnosis.Detail);
    }

    [Fact]
    public void Explain_NoLocalInterfaces_StillProducesAnExplanation()
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            new LocalNetworkProfile([]),
            route: null,
            serviceAnswered: false);

        Assert.Contains("no active IPv4 interface", diagnosis.Detail);
    }

    // The short cause is what the table row shows, and the row has space for nothing else. A long
    // one would be truncated into meaninglessness, which is the failure this split exists to fix.
    [Theory]
    [InlineData("192.168.0.148", "192.168.87.20", "different subnet")]
    [InlineData("192.168.0.148", "192.168.0.50", "no answer on port")]
    public void Explain_ShortCause_FitsInATableRow(string target, string localAddress, string expected)
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse(target),
            ProfileOn(localAddress, "192.168.0.1"),
            route: null,
            serviceAnswered: false);

        Assert.Equal(expected, diagnosis.ShortCause);
        Assert.True(diagnosis.ShortCause.Length <= 24, $"'{diagnosis.ShortCause}' is too long for the status column.");
    }

    [Fact]
    public void ToSingleLine_JoinsTheDetailAndTheRemedy()
    {
        var diagnosis = ReachabilityExplainer.Explain(
            IPAddress.Parse("192.168.0.148"),
            ProfileOn("192.168.87.20", "192.168.87.1"),
            route: null,
            serviceAnswered: false);

        var line = diagnosis.ToSingleLine();

        Assert.StartsWith(diagnosis.Detail, line);
        Assert.EndsWith(diagnosis.Remedy, line);
    }

    [Fact]
    public void ToSingleLine_WithNoRemedy_IsJustTheDetail()
    {
        var diagnosis = new ReachabilityDiagnosis(
            ReachabilityCause.DifferentSubnet,
            "different subnet",
            "Detail with nothing to suggest.");

        Assert.Equal("Detail with nothing to suggest.", diagnosis.ToSingleLine());
    }
}
