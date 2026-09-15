using System.Net;
using LanInspector.Core.Configuration;
using LanInspector.Core.Discovery;
using LanInspector.Core.Identity;
using LanInspector.Core.Locator;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;
using Xunit;

namespace LanInspector.Tests;

public sealed class DeviceLocatorTests
{
    private static KnownDeviceDefinition HomeServer(
        List<string>? macs = null,
        List<string>? ips = null,
        List<string>? tailscaleNames = null) => new()
    {
        Id = "home-server",
        DisplayName = "Home Server",
        KnownMacs = macs ?? [],
        KnownIps = ips ?? ["192.168.0.148"],
        KnownTailscaleNames = tailscaleNames ?? [],
        Ssh = new KnownDeviceSshOptions { Enabled = true, User = "gagneet", Port = 22 }
    };

    private static DeviceLocatorService CreateLocator(
        IReadOnlyList<ArpTableEntry>? arpEntries = null,
        TailscaleStatus? tailscale = null,
        DeviceLocationHistoryStore? history = null,
        IDeviceNetworkInspector? inspector = null,
        INetworkDiscovery? sweeper = null,
        IArpTableReader? arpReader = null)
    {
        return new DeviceLocatorService(
            new FakeTailscaleService(tailscale ?? new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], [])),
            new FakeProfileProvider(),
            arpReader ?? new FakeArpTableReader(arpEntries ?? []),
            new PortScanner(),
            NoDns(),
            history,
            networkInspector: inspector,
            subnetSweeper: sweeper);
    }

    // Real DNS makes results depend on the machine running the tests: on a host named after the
    // device under test, "ubuntu-svr" resolves to that host.
    private static HostnameResolver NoDns() => new((_, _) => Task.FromResult(Array.Empty<IPAddress>()));

    // Probing is disabled in these tests: they assert how evidence is ranked, and nothing in a
    // build environment answers on 192.168.0.x.
    private static readonly DeviceLocatorOptions NoProbe = DeviceLocatorOptions.Default with { VerifyWithTcpProbe = false };

    [Fact]
    public async Task LocateAsync_MacInArpCache_WinsOverStaleConfiguredAddress()
    {
        var locator = CreateLocator(arpEntries:
        [
            new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")
        ]);

        var location = await locator.LocateAsync(
            HomeServer(macs: ["9c:6b:00:aa:bb:cc"], ips: ["192.168.0.148"]),
            NoProbe);

        Assert.Equal("192.168.0.154", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.ArpTable, location.Source);
        Assert.Equal(LocationConfidence.High, location.Confidence);
    }

    [Fact]
    public async Task LocateAsync_NoMacConfigured_SaysSoInEvidence()
    {
        var location = await CreateLocator().LocateAsync(HomeServer(), NoProbe);

        Assert.Contains(location.Evidence, line => line.Contains("knownMacs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocateAsync_MacNotInArpCache_ExplainsWhyAndFallsBack()
    {
        var location = await CreateLocator().LocateAsync(
            HomeServer(macs: ["9c:6b:00:aa:bb:cc"]),
            NoProbe);

        Assert.Contains(location.Evidence, line => line.Contains("layer-2 segment", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LocationSource.ConfiguredAddress, location.Source);
        Assert.Equal(LocationConfidence.Low, location.Confidence);
    }

    [Fact]
    public async Task LocateAsync_TailscaleDirectPath_YieldsLanAddressAndOverlayAddress()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            CurrentAddress: new IPEndPoint(IPAddress.Parse("192.168.0.154"), 41641));

        var locator = CreateLocator(tailscale: new TailscaleStatus(
            TailscaleConnectionState.Connected,
            [peer],
            [IPAddress.Parse("100.64.0.1")],
            "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(ips: ["192.168.0.148"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        Assert.Equal("192.168.0.154", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.TailscaleDirectPath, location.Source);
        Assert.Equal("100.83.183.74", location.TailscaleAddress?.ToString());
        Assert.Equal("ubuntu-svr.tail7f7c1e.ts.net", location.TailscaleName);
    }

    [Fact]
    public async Task LocateAsync_ArpBeatsTailscaleWhenBothAgreeOnDifferentAddresses()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            CurrentAddress: new IPEndPoint(IPAddress.Parse("192.168.0.99"), 41641));

        var locator = CreateLocator(
            arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")],
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(macs: ["9c:6b:00:aa:bb:cc"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        Assert.Equal("192.168.0.154", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.ArpTable, location.Source);
    }

    [Fact]
    public async Task LocateAsync_SameAddressFromSeveralSources_IsReportedOnceWithBestEvidence()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            CurrentAddress: new IPEndPoint(IPAddress.Parse("192.168.0.154"), 41641));

        var locator = CreateLocator(
            arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")],
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(macs: ["9c:6b:00:aa:bb:cc"], ips: ["192.168.0.154"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        var candidate = Assert.Single(location.Candidates, c => c.Address.ToString() == "192.168.0.154");
        Assert.Equal(LocationSource.ArpTable, candidate.Source);
    }

    [Fact]
    public async Task LocateAsync_TailscaleEndpointOnly_RanksBelowDirectPathAndIsMediumConfidence()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            Endpoints: [new IPEndPoint(IPAddress.Parse("192.168.0.154"), 41641)]);

        var locator = CreateLocator(tailscale: new TailscaleStatus(
            TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(ips: [], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        Assert.Equal(LocationSource.TailscaleEndpoint, location.Source);
        Assert.Equal(LocationConfidence.Medium, location.Confidence);
    }

    [Fact]
    public async Task LocateAsync_TailscalePingProbe_IsOnlyRunWhenRequested()
    {
        var peer = new TailscaleDevice("ubuntu-svr", "ubuntu-svr.tail7f7c1e.ts.net", [IPAddress.Parse("100.83.183.74")], IsOnline: true);
        var status = new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop");
        var tailscale = new FakeTailscaleService(status, new IPEndPoint(IPAddress.Parse("192.168.0.154"), 41641));

        var locator = new DeviceLocatorService(
            tailscale,
            new FakeProfileProvider(),
            new FakeArpTableReader([]),
            new PortScanner(),
            NoDns());

        var device = HomeServer(ips: [], tailscaleNames: ["ubuntu-svr"]);

        var withoutProbe = await locator.LocateAsync(device, NoProbe);
        Assert.Equal(0, tailscale.PingCallCount);
        Assert.Null(withoutProbe.CurrentAddress);

        var withProbe = await locator.LocateAsync(device, NoProbe with { UseTailscalePingProbe = true });
        Assert.Equal(1, tailscale.PingCallCount);
        Assert.Equal("192.168.0.154", withProbe.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.TailscalePing, withProbe.Source);
    }

    [Fact]
    public async Task LocateAsync_NothingFound_ReportsNoneAndStillOffersTailscale()
    {
        var peer = new TailscaleDevice("ubuntu-svr", "ubuntu-svr.tail7f7c1e.ts.net", [IPAddress.Parse("100.83.183.74")], IsOnline: false);
        var locator = CreateLocator(tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var location = await locator.LocateAsync(HomeServer(ips: [], tailscaleNames: ["ubuntu-svr"]), NoProbe);

        Assert.Null(location.CurrentAddress);
        Assert.Equal(LocationConfidence.None, location.Confidence);
        Assert.Equal("100.83.183.74", location.TailscaleAddress?.ToString());
        Assert.Contains("Tailscale", location.Summary);
    }

    [Fact]
    public async Task LocateAsync_RecordsMoveAgainstHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-locate-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DeviceLocationHistoryStore(path);
            store.Record("home-server", IPAddress.Parse("192.168.0.148"), LocationSource.ArpTable, DateTimeOffset.UtcNow.AddDays(-1));

            var locator = CreateLocator(
                arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")],
                history: store);

            var location = await locator.LocateAsync(HomeServer(macs: ["9c:6b:00:aa:bb:cc"]), NoProbe);

            Assert.True(location.HasMoved);
            Assert.Equal("192.168.0.148", location.PreviousAddress?.ToString());
            Assert.Contains("changed from 192.168.0.148", location.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LocateAllAsync_ReadsArpAndTailscaleOncePerBatch()
    {
        var arp = new FakeArpTableReader([]);
        var tailscale = new FakeTailscaleService(new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []));

        var locator = new DeviceLocatorService(
            tailscale, new FakeProfileProvider(), arp, new PortScanner(), NoDns());

        var devices = new[] { "first", "second", "third" }
            .Select(id => new KnownDeviceDefinition { Id = id, DisplayName = id, KnownIps = ["192.168.0.148"] })
            .ToArray();

        var results = await locator.LocateAllAsync(devices, NoProbe);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, arp.ReadCallCount);
        Assert.Equal(1, tailscale.StatusCallCount);
    }

    [Fact]
    public async Task LocateAsync_RecentlyRememberedAddress_OutranksTheConfiguredOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-recent-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DeviceLocationHistoryStore(path);
            store.Record("home-server", IPAddress.Parse("192.168.0.154"), LocationSource.ArpTable, DateTimeOffset.UtcNow.AddHours(-2));

            var location = await CreateLocator(history: store).LocateAsync(
                HomeServer(ips: ["192.168.0.148"]),
                NoProbe);

            Assert.Equal("192.168.0.154", location.CurrentAddress?.ToString());
            Assert.Equal(LocationSource.PreviousLocation, location.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LocateAsync_StaleRememberedAddress_IsNotOfferedAtAll()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-stale-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DeviceLocationHistoryStore(path);
            store.Record("home-server", IPAddress.Parse("192.168.0.9"), LocationSource.ArpTable, DateTimeOffset.UtcNow.AddDays(-30));

            var location = await CreateLocator(history: store).LocateAsync(
                HomeServer(ips: ["192.168.0.148"]),
                NoProbe);

            Assert.Equal("192.168.0.148", location.CurrentAddress?.ToString());
            Assert.Equal(LocationSource.ConfiguredAddress, location.Source);
            Assert.DoesNotContain(location.Candidates, candidate => candidate.Address.ToString() == "192.168.0.9");
            Assert.Contains(location.Evidence, line => line.Contains("too stale", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LocateAsync_FirstSighting_ReportsNoMoveAndNoChangeTimestamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laninspector-first-{Guid.NewGuid():N}.json");
        try
        {
            var locator = CreateLocator(
                arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")],
                history: new DeviceLocationHistoryStore(path));

            var location = await locator.LocateAsync(HomeServer(macs: ["9c:6b:00:aa:bb:cc"]), NoProbe);

            Assert.False(location.HasMoved);
            Assert.Null(location.PreviousAddress);
            Assert.Null(location.AddressChangedAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LocateAsync_PeerContainerBridges_DoNotOutrankTheRealLanAddress()
    {
        // ubuntu-svr runs Docker and Kubernetes, so Tailscale advertises its bridge addresses
        // (10.20.x.1) alongside the real one. Those are RFC1918 and real on the peer, but
        // unreachable from here, and must not be reported as the server's address.
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            Endpoints:
            [
                new IPEndPoint(IPAddress.Parse("10.20.4.1"), 41641),
                new IPEndPoint(IPAddress.Parse("10.20.2.1"), 41641)
            ]);

        var locator = CreateLocator(tailscale: new TailscaleStatus(
            TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var device = HomeServer(ips: ["192.168.0.148"], tailscaleNames: ["ubuntu-svr"]);
        var location = await locator.LocateAsync(device, NoProbe);

        Assert.Equal("192.168.0.148", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.ConfiguredAddress, location.Source);

        var bridge = location.Candidates.Single(candidate => candidate.Address.ToString() == "10.20.4.1");
        Assert.Equal(CandidatePlausibility.Unrelated, bridge.Plausibility);
        Assert.Contains(location.Evidence, line => line.Contains("10.20.4.1") && line.Contains("container bridge"));
    }

    [Fact]
    public async Task LocateAsync_AddressOnALocalSubnet_OutranksAnOffSubnetOne()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            Endpoints: [new IPEndPoint(IPAddress.Parse("10.20.4.1"), 41641)]);

        var locator = CreateLocator(
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        // The fake profile puts this machine on 192.168.0.0/24.
        var device = HomeServer(ips: ["192.168.0.148"], tailscaleNames: ["ubuntu-svr"]);
        var location = await locator.LocateAsync(device, NoProbe);

        Assert.Equal(CandidatePlausibility.OnLocalSubnet, location.Candidates[0].Plausibility);
        Assert.Equal("192.168.0.148", location.Candidates[0].Address.ToString());
    }

    [Fact]
    public async Task LocateAsync_ConfiguredSubnetForTheDevice_RanksAboveAnUnrelatedAddress()
    {
        var device = new KnownDeviceDefinition
        {
            Id = "mac-mini",
            DisplayName = "Mac Mini",
            // Wi-Fi side, not on this machine's subnet, but declared for the device.
            KnownIps = ["192.168.87.118"],
            KnownSubnets = ["192.168.87.0/24"],
            KnownTailscaleNames = ["gagneets-mac-mini"]
        };

        var peer = new TailscaleDevice(
            "gagneets-mac-mini",
            "gagneets-mac-mini.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.64.0.9")],
            IsOnline: true,
            Endpoints: [new IPEndPoint(IPAddress.Parse("10.20.9.1"), 41641)]);

        var locator = CreateLocator(tailscale: new TailscaleStatus(
            TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        var location = await locator.LocateAsync(device, NoProbe);

        var wifi = location.Candidates.Single(candidate => candidate.Address.ToString() == "192.168.87.118");
        var bridge = location.Candidates.Single(candidate => candidate.Address.ToString() == "10.20.9.1");

        Assert.Equal(CandidatePlausibility.ConfiguredForDevice, wifi.Plausibility);
        Assert.Equal(CandidatePlausibility.Unrelated, bridge.Plausibility);
        Assert.Equal("192.168.87.118", location.CurrentAddress?.ToString());
    }

    [Fact]
    public async Task LocateAsync_LeaseMovedWithinAConfiguredSubnet_IsStillPlausible()
    {
        var peer = new TailscaleDevice(
            "ubuntu-svr",
            "ubuntu-svr.tail7f7c1e.ts.net",
            [IPAddress.Parse("100.83.183.74")],
            IsOnline: true,
            CurrentAddress: new IPEndPoint(IPAddress.Parse("172.30.5.9"), 41641));

        var locator = CreateLocator(tailscale: new TailscaleStatus(
            TailscaleConnectionState.Connected, [peer], [], "my-laptop"));

        // 172.30.5.9 shares a /24 with the configured 172.30.5.40, so it reads as the same subnet.
        var device = HomeServer(ips: ["172.30.5.40"], tailscaleNames: ["ubuntu-svr"]);
        var location = await locator.LocateAsync(device, NoProbe);

        var moved = location.Candidates.Single(candidate => candidate.Address.ToString() == "172.30.5.9");
        Assert.Equal(CandidatePlausibility.ConfiguredForDevice, moved.Plausibility);
    }

    [Fact]
    public async Task LocateAsync_DirectPathEndsAtSomeoneElsesMac_IsTheRouterInFrontNotTheDevice()
    {
        // From outside a NAT router, Tailscale's direct path to a device behind it ends at the
        // router's outside address, and this machine's ARP cache holds the router's MAC there.
        var locator = CreateLocator(
            arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.23"), "44ADB1D63959", "eth0", "REACHABLE")],
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [ServerPeerAt("192.168.0.23")], [], "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(macs: ["68:1d:ef:3c:d5:45"], ips: ["10.0.5.148"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        Assert.Equal("192.168.0.23", location.NatAddress?.ToString());
        Assert.DoesNotContain(location.Candidates, candidate => candidate.Address.ToString() == "192.168.0.23");
        Assert.Equal("10.0.5.148", location.CurrentAddress?.ToString());
        Assert.Contains(location.Evidence, line => line.Contains("sits behind"));
    }

    [Fact]
    public async Task LocateAsync_DirectPathWithARandomisedMac_IsNotMistakenForARouter()
    {
        // The device itself may use a private MAC on one interface, so it proves nothing.
        var locator = CreateLocator(
            arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.23"), "86617AC61F7F", "eth0", "REACHABLE")],
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [ServerPeerAt("192.168.0.23")], [], "my-laptop"));

        var location = await locator.LocateAsync(
            HomeServer(macs: ["68:1d:ef:3c:d5:45"], ips: ["10.0.5.148"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe);

        Assert.Null(location.NatAddress);
        Assert.Equal("192.168.0.23", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.TailscaleDirectPath, location.Source);
    }

    [Fact]
    public async Task LocateAsync_DeviceReportsItsNetwork_ReplacesTheGuesses()
    {
        var inspector = new FakeInspector(new DeviceNetworkInspection(DeviceNetworkReportParser.Parse(ReportBehindRouter), null));
        var locator = CreateLocator(
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [ServerPeerAt("192.168.0.23")], [], "my-laptop"),
            inspector: inspector);

        var location = await locator.LocateAsync(
            HomeServer(ips: ["10.0.9.9"], tailscaleNames: ["ubuntu-svr"]),
            NoProbe with { InspectOverSsh = true });

        Assert.Equal(("gagneet", "100.83.183.74", 22, true), inspector.LastCall);
        Assert.Equal("10.0.5.148", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.DeviceReported, location.Source);
        Assert.Equal(LocationConfidence.High, location.Confidence);
        Assert.Equal("192.168.0.23", location.NatAddress?.ToString());
        Assert.Equal(["10.0.5.0/24"], location.UnapprovedRoutes);
        Assert.NotNull(location.NetworkReport);
        Assert.DoesNotContain(location.Candidates, candidate => candidate.Address.ToString() is "10.0.9.9" or "192.168.0.23");
    }

    [Fact]
    public async Task LocateAsync_ApprovedRoute_IsNotReportedAsUnapproved()
    {
        var peer = ServerPeerAt("192.168.0.23") with { PrimaryRoutes = ["10.0.5.0/24"] };
        var locator = CreateLocator(
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [peer], [], "my-laptop"),
            inspector: new FakeInspector(new DeviceNetworkInspection(DeviceNetworkReportParser.Parse(ReportBehindRouter), null)));

        var location = await locator.LocateAsync(
            HomeServer(tailscaleNames: ["ubuntu-svr"]),
            NoProbe with { InspectOverSsh = true });

        Assert.Empty(location.UnapprovedRoutes);
    }

    [Fact]
    public async Task LocateAsync_NoSshProfile_NeverLogsIn()
    {
        var inspector = new FakeInspector(DeviceNetworkInspection.Failed("should not be called"));
        var locator = CreateLocator(
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [ServerPeerAt("192.168.0.23")], [], "my-laptop"),
            inspector: inspector);

        var device = new KnownDeviceDefinition { Id = "nas", DisplayName = "NAS", KnownTailscaleNames = ["ubuntu-svr"] };
        await locator.LocateAsync(device, NoProbe with { InspectOverSsh = true });

        Assert.Equal(0, inspector.CallCount);
    }

    [Fact]
    public async Task LocateAsync_SshInspectionFails_SaysWhyAndKeepsTheOtherEvidence()
    {
        var locator = CreateLocator(
            tailscale: new TailscaleStatus(TailscaleConnectionState.Connected, [ServerPeerAt("192.168.0.23")], [], "my-laptop"),
            inspector: new FakeInspector(DeviceNetworkInspection.Failed("nothing accepts SSH connections on 100.83.183.74 port 22.")));

        var location = await locator.LocateAsync(
            HomeServer(tailscaleNames: ["ubuntu-svr"]),
            NoProbe with { InspectOverSsh = true });

        Assert.Contains(location.Evidence, line => line.StartsWith("Could not ask home-server over SSH: nothing accepts SSH"));
        Assert.Null(location.NetworkReport);
        Assert.Equal("192.168.0.23", location.CurrentAddress?.ToString());
    }

    [Fact]
    public async Task LocateAsync_MovedDeviceMissingFromArpCache_IsFoundBySweepingTheSubnet()
    {
        // After a router restart the device has a new lease and nothing has spoken to it, so the
        // cache has no entry until the sweep makes this machine resolve every address.
        var arp = new FakeArpTableReader([], [new ArpTableEntry(IPAddress.Parse("192.168.0.77"), "9C6B00AABBCC", "eth0", "REACHABLE")]);
        var sweeper = new FakeSweeper();
        var locator = CreateLocator(arpReader: arp, sweeper: sweeper);

        var location = await locator.LocateAsync(
            HomeServer(macs: ["9c:6b:00:aa:bb:cc"], ips: ["192.168.0.148"]),
            NoProbe with { SweepLocalSubnets = true });

        Assert.Equal(["192.168.0.0/24"], sweeper.Swept);
        Assert.Equal("192.168.0.77", location.CurrentAddress?.ToString());
        Assert.Equal(LocationSource.ArpTable, location.Source);
    }

    [Fact]
    public async Task LocateAllAsync_SweepsAtMostOncePerBatch()
    {
        var sweeper = new FakeSweeper();
        var locator = CreateLocator(sweeper: sweeper);

        var devices = new[] { "aa:aa:aa:aa:aa:01", "aa:aa:aa:aa:aa:02" }
            .Select(mac => new KnownDeviceDefinition { Id = mac, DisplayName = mac, KnownMacs = [mac] })
            .ToArray();

        await locator.LocateAllAsync(devices, NoProbe with { SweepLocalSubnets = true });

        Assert.Single(sweeper.Swept);
    }

    [Fact]
    public async Task LocateAsync_MacAlreadyInArpCache_DoesNotSweep()
    {
        var sweeper = new FakeSweeper();
        var locator = CreateLocator(
            arpEntries: [new ArpTableEntry(IPAddress.Parse("192.168.0.154"), "9C6B00AABBCC", "eth0", "REACHABLE")],
            sweeper: sweeper);

        await locator.LocateAsync(HomeServer(macs: ["9c:6b:00:aa:bb:cc"]), NoProbe with { SweepLocalSubnets = true });

        Assert.Empty(sweeper.Swept);
    }

    // A server on 10.0.5.0/24 behind a router that sits on this machine's 192.168.0.0/24.
    private const string ReportBehindRouter =
        "### os\nLinux\n### host\nubuntu-svr\n" +
        "### ip-addr\n2: enp2s0    inet 10.0.5.148/24 brd 10.0.5.255 scope global enp2s0\n" +
        "### ip-route\ndefault via 10.0.5.1 dev enp2s0\n" +
        "### trace\n 1  10.0.5.1  0.3 ms\n 2  192.168.0.1  0.6 ms\n" +
        "### tailscale-prefs\n\t\"AdvertiseRoutes\": [\n\t\t\"10.0.5.0/24\"\n\t],\n";

    private static TailscaleDevice ServerPeerAt(string directPath) => new(
        "ubuntu-svr",
        "ubuntu-svr.tail7f7c1e.ts.net",
        [IPAddress.Parse("100.83.183.74")],
        IsOnline: true,
        CurrentAddress: new IPEndPoint(IPAddress.Parse(directPath), 41641));

    private sealed class FakeInspector(DeviceNetworkInspection result) : IDeviceNetworkInspector
    {
        public int CallCount { get; private set; }

        public (string User, string Host, int Port, bool Authenticated)? LastCall { get; private set; }

        public Task<DeviceNetworkInspection> InspectAsync(string user, string host, int port, bool hostIsAuthenticated, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCall = (user, host, port, hostIsAuthenticated);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSweeper : INetworkDiscovery
    {
        public List<string> Swept { get; } = [];

        public Task<IReadOnlyCollection<IPAddress>> PingSweepAsync(IPAddress subnet, int cidr, CancellationToken cancellationToken = default)
        {
            Swept.Add($"{subnet}/{cidr}");
            return Task.FromResult<IReadOnlyCollection<IPAddress>>([]);
        }
    }

    /// <param name="later">What every read after the first returns, as if something refreshed the cache.</param>
    private sealed class FakeArpTableReader(IReadOnlyList<ArpTableEntry> entries, IReadOnlyList<ArpTableEntry>? later = null) : IArpTableReader
    {
        public int ReadCallCount { get; private set; }

        public Task<IReadOnlyList<ArpTableEntry>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            return Task.FromResult(ReadCallCount > 1 && later is not null ? later : entries);
        }
    }

    private sealed class FakeTailscaleService(TailscaleStatus status, IPEndPoint? directEndpoint = null) : ITailscaleService
    {
        public int StatusCallCount { get; private set; }

        public int PingCallCount { get; private set; }

        public Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCallCount++;
            return Task.FromResult(status);
        }

        public Task<IPEndPoint?> TryGetDirectEndpointAsync(string target, CancellationToken cancellationToken = default)
        {
            PingCallCount++;
            return Task.FromResult(directEndpoint);
        }
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