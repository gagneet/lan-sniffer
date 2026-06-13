using System.Net;
using LanInspector.Core.Configuration;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Topology;
using Xunit;

namespace LanInspector.Tests;

public sealed class TopologyBuilderTests
{
    private static LocalNetworkProfile BuildProfile(string ip, string gateway)
    {
        var addr = IPAddress.Parse(ip);
        var gw = IPAddress.Parse(gateway);
        var network = new IPv4Network(addr, 24);
        return new LocalNetworkProfile([new LocalNetworkInterface("eth0", "Ethernet", addr, network, gw)]);
    }

    [Fact]
    public void AddLocalProfile_AddsThisMachineAndGatewayNodes()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var snapshot = new TopologyBuilder().AddLocalProfile(profile).Build();

        Assert.Contains(snapshot.Nodes, n => n.Type == NetworkNodeType.ThisMachine);
        Assert.Contains(snapshot.Nodes, n => n.Type == NetworkNodeType.Gateway);
        Assert.Equal(2, snapshot.Nodes.Count);
    }

    [Fact]
    public void AddLocalProfile_AddsRouterEdge()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var snapshot = new TopologyBuilder().AddLocalProfile(profile).Build();

        Assert.Single(snapshot.Edges);
        Assert.Equal(TopologyLinkType.Routed, snapshot.Edges[0].LinkType);
    }

    [Fact]
    public void AddLocalProfile_GatewayNode_HasConfirmedConfidence()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var snapshot = new TopologyBuilder().AddLocalProfile(profile).Build();

        var gw = snapshot.Nodes.First(n => n.Type == NetworkNodeType.Gateway);
        Assert.Equal(TopologyConfidence.High, gw.Confidence);
    }

    [Fact]
    public void AddKnownDevices_AddsKnownDeviceNode()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var knownDevices = new List<KnownDeviceDefinition>
        {
            new() { Id = "server", DisplayName = "Home Server", KnownIps = ["192.168.2.50"] }
        };

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddKnownDevices(knownDevices, [])
            .Build();

        Assert.Contains(snapshot.Nodes, n => n.Id == "known:server");
    }

    [Fact]
    public void AddTailscaleStatus_ConnectedPeer_AddsTailscaleEdge()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var peer = new TailscaleDevice("peer-server", "peer-server.ts.net", [IPAddress.Parse("100.64.0.2")], true);
        var ts = new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [IPAddress.Parse("100.64.0.1")], "my-machine");

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddTailscaleStatus(ts)
            .Build();

        Assert.Contains(snapshot.Edges, e => e.LinkType == TopologyLinkType.Tailscale);
    }

    [Fact]
    public void AddTailscaleStatus_NotConnected_NoTailscaleEdge()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var ts = new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []);

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddTailscaleStatus(ts)
            .Build();

        Assert.DoesNotContain(snapshot.Edges, e => e.LinkType == TopologyLinkType.Tailscale);
    }

    [Fact]
    public void Build_MermaidOutput_ContainsNodes()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var snapshot = new TopologyBuilder().AddLocalProfile(profile).Build();

        var mermaid = snapshot.ToMermaid();
        Assert.Contains("graph LR", mermaid);
        Assert.Contains("Gateway", mermaid);
    }

    [Fact]
    public void AddDiscoveredDevices_ArpDevice_AddsNodeWithMediumConfidence()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var device = new Device
        {
            MacAddress = "aa:bb:cc:dd:ee:ff",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };
        device.IpAddresses.Add("192.168.1.50");
        device.SeenVia.Add("ARP");

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddDiscoveredDevices([device], profile)
            .Build();

        var devNode = snapshot.Nodes.FirstOrDefault(n => n.MacAddress == "aa:bb:cc:dd:ee:ff");
        Assert.NotNull(devNode);
        Assert.Equal(TopologyConfidence.Medium, devNode.Confidence);
    }

    [Fact]
    public void AddDiscoveredDevices_ArpDhcpDevice_AddsNodeWithHighConfidence()
    {
        var profile = BuildProfile("192.168.1.100", "192.168.1.1");
        var device = new Device
        {
            MacAddress = "aa:bb:cc:dd:ee:ff",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };
        device.IpAddresses.Add("192.168.1.50");
        device.SeenVia.Add("ARP");
        device.SeenVia.Add("DHCP");

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddDiscoveredDevices([device], profile)
            .Build();

        var devNode = snapshot.Nodes.First(n => n.MacAddress == "aa:bb:cc:dd:ee:ff");
        Assert.Equal(TopologyConfidence.High, devNode.Confidence);
    }
}
