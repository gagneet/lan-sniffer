using System.Net;
using LanInspector.Core.Configuration;
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
        DeviceLocationHistoryStore? history = null)
    {
        return new DeviceLocatorService(
            new FakeTailscaleService(tailscale ?? new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], [])),
            new FakeProfileProvider(),
            new FakeArpTableReader(arpEntries ?? []),
            new PortScanner(),
            new HostnameResolver(),
            history);
    }

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
            new HostnameResolver());

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
            tailscale, new FakeProfileProvider(), arp, new PortScanner(), new HostnameResolver());

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

    private sealed class FakeArpTableReader(IReadOnlyList<ArpTableEntry> entries) : IArpTableReader
    {
        public int ReadCallCount { get; private set; }

        public Task<IReadOnlyList<ArpTableEntry>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            return Task.FromResult(entries);
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