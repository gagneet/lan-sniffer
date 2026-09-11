using System.Net;
using LanInspector.Core.Analysis;
using LanInspector.Core.Configuration;
using LanInspector.Core.Identity;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using Xunit;

namespace LanInspector.Tests;

public sealed class DeviceNameRegistryTests
{
    private static readonly KnownDeviceDefinition HomeServer = new()
    {
        Id = "home-server",
        DisplayName = "Home Server (ubuntu-svr)",
        KnownIps = ["192.168.0.148"],
        KnownMacs = ["68:1d:ef:3c:d5:45"]
    };

    private static Device CapturedDevice(string mac, string ip, string? hostname = null)
    {
        var device = new Device { MacAddress = mac, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
        device.IpAddresses.Add(ip);
        device.Hostname = hostname;
        return device;
    }

    [Fact]
    public void Resolve_ConfiguredAddress_ReturnsDisplayName()
    {
        var registry = new DeviceNameRegistry([HomeServer]);

        Assert.Equal("Home Server (ubuntu-svr)", registry.Resolve("192.168.0.148"));
    }

    [Fact]
    public void Resolve_UnknownAddress_ReturnsNull()
    {
        var registry = new DeviceNameRegistry([HomeServer]);

        Assert.Null(registry.Resolve("8.8.8.8"));
        Assert.Null(registry.Resolve(""));
    }

    [Fact]
    public void Resolve_CapturedDeviceHostname_IsUsedWhenNothingIsConfigured()
    {
        var devices = new List<Device> { CapturedDevice("AABBCCDDEEFF", "192.168.0.77", "printer.local") };
        var registry = new DeviceNameRegistry([], () => devices);

        Assert.Equal("printer.local", registry.Resolve("192.168.0.77"));
    }

    [Fact]
    public void Resolve_KnownDeviceMatchedByMac_NamesWhicheverAddressItNowHolds()
    {
        // The lease moved to .154 but the config still says .148; the MAC carries the name across.
        var devices = new List<Device> { CapturedDevice("681DEF3CD545", "192.168.0.154", "ubuntu-svr") };
        var registry = new DeviceNameRegistry([HomeServer], () => devices);

        Assert.Equal("Home Server (ubuntu-svr)", registry.Resolve("192.168.0.154"));
    }

    [Fact]
    public void Resolve_ConfiguredName_OutranksTheCapturedHostname()
    {
        var devices = new List<Device> { CapturedDevice("681DEF3CD545", "192.168.0.148", "ubuntu-svr") };
        var registry = new DeviceNameRegistry([HomeServer], () => devices);

        Assert.Equal("Home Server (ubuntu-svr)", registry.Resolve("192.168.0.148"));
    }

    [Fact]
    public void Resolve_DnsAnswerForAnExternalHost_NamesAnAddressNoDeviceOwns()
    {
        var registry = new DeviceNameRegistry([]);

        registry.RecordDnsName(new DnsNameObservation("52.10.20.30", "example.com", FromMulticast: false));

        Assert.Equal("example.com", registry.Resolve("52.10.20.30"));
    }

    [Fact]
    public void Resolve_DeviceName_OutranksADnsAnswerForTheSameAddress()
    {
        var registry = new DeviceNameRegistry([HomeServer]);

        registry.RecordDnsName(new DnsNameObservation("192.168.0.148", "ubuntu-svr.lan", FromMulticast: false));

        Assert.Equal("Home Server (ubuntu-svr)", registry.Resolve("192.168.0.148"));
    }

    [Fact]
    public void Resolve_SeveralNamesForOneAddress_KeepsTheFirstSoRowsDoNotFlicker()
    {
        var registry = new DeviceNameRegistry([]);

        registry.RecordDnsName(new DnsNameObservation("52.10.20.30", "first.example.com", FromMulticast: false));
        registry.RecordDnsName(new DnsNameObservation("52.10.20.30", "second.example.com", FromMulticast: false));

        Assert.Equal("first.example.com", registry.Resolve("52.10.20.30"));
    }

    [Fact]
    public void Observe_RealDnsAnswerPacket_ReachesTheRegistry()
    {
        // Drives the whole path the app uses: capture -> DnsAnalyzer -> NameObserved -> registry.
        var analyzer = new DnsAnalyzer([]);
        var registry = new DeviceNameRegistry([]);
        registry.Observe(analyzer);

        analyzer.Analyze(DnsPacketBuilder.AnswerFor("example.com", "52.10.20.30"));

        Assert.Equal("example.com", registry.Resolve("52.10.20.30"));
    }

    [Fact]
    public void Observe_MdnsAnswer_NamesALocalDevice()
    {
        var analyzer = new DnsAnalyzer([]);
        var registry = new DeviceNameRegistry([]);
        registry.Observe(analyzer);

        analyzer.Analyze(DnsPacketBuilder.AnswerFor("gagneets-mac-mini.local", "192.168.0.154", sourcePort: 5353));

        Assert.Equal("gagneets-mac-mini.local", registry.Resolve("192.168.0.154"));
    }

    [Fact]
    public void StopObserving_DetachesFromTheAnalyzer()
    {
        var analyzer = new DnsAnalyzer([]);
        var registry = new DeviceNameRegistry([]);
        registry.Observe(analyzer);
        registry.StopObserving(analyzer);

        analyzer.Analyze(DnsPacketBuilder.AnswerFor("example.com", "52.10.20.30"));

        Assert.Null(registry.Resolve("52.10.20.30"));
    }

    [Fact]
    public void Resolve_TailscalePeer_NamesItsOverlayAddress()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true);

        var registry = new DeviceNameRegistry([]);
        registry.SetTailscaleStatus(new TailscaleStatus(
            TailscaleConnectionState.Connected,
            [peer],
            [IPAddress.Parse("100.64.0.1")],
            "my-laptop"));

        Assert.Equal("ubuntu-svr", registry.Resolve("100.83.183.74"));
        Assert.Equal("This machine", registry.Resolve("100.64.0.1"));
    }

    [Fact]
    public void Resolve_TailscaleNotConnected_ContributesNoNames()
    {
        var registry = new DeviceNameRegistry([]);
        registry.SetTailscaleStatus(new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []));

        Assert.Null(registry.Resolve("100.83.183.74"));
    }

    [Fact]
    public void Resolve_LocalInterfaceAndGateway_AreLabelled()
    {
        var registry = new DeviceNameRegistry([], null, new FakeProfileProvider());

        Assert.Equal("This machine (eth0)", registry.Resolve("192.168.0.50"));
        Assert.Equal("Gateway", registry.Resolve("192.168.0.1"));
        Assert.Equal("Broadcast", registry.Resolve("255.255.255.255"));
    }

    [Fact]
    public void Resolve_ConfiguredRouter_OutranksTheGenericGatewayLabel()
    {
        var router = new KnownDeviceDefinition
        {
            Id = "fast5366lte-a",
            DisplayName = "FAST5366LTE-A / Optus Modem",
            KnownIps = ["192.168.0.1"]
        };

        var registry = new DeviceNameRegistry([router], null, new FakeProfileProvider());

        Assert.Equal("FAST5366LTE-A / Optus Modem", registry.Resolve("192.168.0.1"));
    }

    [Fact]
    public void Invalidate_PicksUpDevicesCapturedSinceTheLastSnapshot()
    {
        var devices = new List<Device>();
        var registry = new DeviceNameRegistry([], () => devices);

        Assert.Null(registry.Resolve("192.168.0.77"));

        devices.Add(CapturedDevice("AABBCCDDEEFF", "192.168.0.77", "printer.local"));
        registry.Invalidate();

        Assert.Equal("printer.local", registry.Resolve("192.168.0.77"));
    }

    private sealed class FakeProfileProvider : ILocalNetworkProfileProvider
    {
        public LocalNetworkProfile GetCurrentProfile() => new(
        [
            new LocalNetworkInterface(
                "eth0",
                "Test adapter",
                IPAddress.Parse("192.168.0.50"),
                IPv4Network.FromAddressAndPrefix(IPAddress.Parse("192.168.0.0"), 24),
                IPAddress.Parse("192.168.0.1"))
        ]);
    }
}
