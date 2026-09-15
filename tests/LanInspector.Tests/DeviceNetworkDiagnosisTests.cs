using System.Net;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Locator;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using Xunit;

namespace LanInspector.Tests;

/// <summary>
/// Asking a device how it is connected: parsing its answer, logging in safely, and explaining a
/// device that sits behind a router of its own.
/// </summary>
public sealed class DeviceNetworkDiagnosisTests
{
    // SshNetworkInspector.Script run on a Linux server with Docker and Kubernetes, trimmed to a few
    // of each kind of virtual interface. It sits behind 192.168.0.1, which hangs off 192.168.87.1.
    internal const string LinuxOutput = """
        ### os
        Linux
        ### host
        ubuntu-svr
        ### ip-addr
        1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
        1: lo    inet6 ::1/128 scope host noprefixroute \       valid_lft forever preferred_lft forever
        2: enp2s0    inet 192.168.0.148/24 metric 100 brd 192.168.0.255 scope global dynamic enp2s0\       valid_lft 83614sec preferred_lft 83614sec
        2: enp2s0    inet6 fdc3:e1c9:2a8b:aa06:6a1d:efff:fe3c:d545/64 scope global dynamic mngtmpaddr noprefixroute \       valid_lft 1754sec preferred_lft 1754sec
        2: enp2s0    inet6 fe80::6a1d:efff:fe3c:d545/64 scope link \       valid_lft forever preferred_lft forever
        4: tailscale0    inet 100.83.183.74/32 scope global tailscale0\       valid_lft forever preferred_lft forever
        4: tailscale0    inet6 fd7a:115c:a1e0::83b:b74b/128 scope global \       valid_lft forever preferred_lft forever
        5: br-790344c95f0e    inet 10.20.5.1/24 brd 10.20.5.255 scope global br-790344c95f0e\       valid_lft forever preferred_lft forever
        7: docker_gwbridge    inet 10.20.1.1/24 brd 10.20.1.255 scope global docker_gwbridge\       valid_lft forever preferred_lft forever
        11: docker0    inet 10.20.0.1/24 brd 10.20.0.255 scope global docker0\       valid_lft forever preferred_lft forever
        12: veth5bcd175    inet6 fe80::ac6f:5cff:fe2c:1e2b/64 scope link \       valid_lft forever preferred_lft forever
        47: calid5777c7f1db    inet6 fe80::ecee:eeff:feee:eeee/64 scope link \       valid_lft forever preferred_lft forever
        ### ip-link
        1: lo: <LOOPBACK,UP,LOWER_UP> mtu 65536 qdisc noqueue state UNKNOWN mode DEFAULT group default qlen 1000\    link/loopback 00:00:00:00:00:00 brd 00:00:00:00:00:00
        2: enp2s0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc fq_codel state UP mode DEFAULT group default qlen 1000\    link/ether 68:1d:ef:3c:d5:45 brd ff:ff:ff:ff:ff:ff
        3: wlp3s0: <BROADCAST,MULTICAST> mtu 1500 qdisc noop state DOWN mode DEFAULT group default qlen 1000\    link/ether c8:8a:d8:0e:3a:1e brd ff:ff:ff:ff:ff:ff
        4: tailscale0: <POINTOPOINT,MULTICAST,NOARP,UP,LOWER_UP> mtu 1280 qdisc fq_codel state UNKNOWN mode DEFAULT group default qlen 500\    link/none
        12: veth5bcd175@if2: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue master br-88c29693c4a9 state UP mode DEFAULT group default \    link/ether ae:6f:5c:2c:1e:2b brd ff:ff:ff:ff:ff:ff link-netnsid 0
        ### ip-route
        default via 192.168.0.1 dev enp2s0 proto dhcp src 192.168.0.148 metric 100
        ### neigh
        192.168.0.154 dev enp2s0 lladdr 1c:f6:4c:51:76:d3 STALE
        192.168.0.1 dev enp2s0 lladdr 44:ad:b1:d6:39:59 REACHABLE
        10.1.0.250 dev calid5777c7f1db lladdr 46:68:d6:05:4a:58 REACHABLE
        ### trace
        traceroute to 1.1.1.1 (1.1.1.1), 4 hops max, 60 byte packets
         1  192.168.0.1  0.272 ms
         2  192.168.87.1  0.510 ms
         3  192.168.4.1  1.329 ms
         4  *
        ### tailscale-prefs
        	"AdvertiseRoutes": [
        		"192.168.0.0/24"
        	],
        """;

    // Hand-written from macOS ifconfig: wired and Wi-Fi on different subnets, a VM bridge, AirDrop
    // and a Tailscale tunnel.
    private const string MacOutput = """
        ### os
        Darwin
        ### host
        mac-mini.local
        ### ifconfig
        lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> mtu 16384
        	inet 127.0.0.1 netmask 0xff000000
        	inet6 ::1 prefixlen 128
        	inet6 fe80::1%lo0 prefixlen 64 scopeid 0x1
        en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	options=50b<RXCSUM,TXCSUM,VLAN_HWTAGGING,AV,CHANNEL_IO>
        	ether 1c:f6:4c:51:76:d3
        	inet6 fe80::1c8b:2b1f:a0c3:1e4d%en0 prefixlen 64 secured scopeid 0x4
        	inet 192.168.0.154 netmask 0xffffff00 broadcast 192.168.0.255
        	inet6 fdc3:e1c9:2a8b:aa06:1c8b:2b1f:a0c3:1e4d prefixlen 64 autoconf secured
        	inet6 fdc3:e1c9:2a8b:aa06:8d2:5a1c:9e0b:77f1 prefixlen 64 autoconf temporary
        	status: active
        en1: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 86:61:7a:c6:1f:7f
        	inet 192.168.87.118 netmask 0xffffff00 broadcast 192.168.87.255
        	status: active
        bridge100: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
        	ether 1e:f6:4c:3e:a1:64
        	inet 192.168.64.1 netmask 0xffffff00 broadcast 192.168.64.255
        awdl0: flags=8943<UP,BROADCAST,RUNNING,PROMISC,SIMPLEX,MULTICAST> mtu 1500
        	ether 3a:2f:1e:5b:7c:90
        	inet6 fe80::382f:1eff:fe5b:7c90%awdl0 prefixlen 64 scopeid 0xb
        utun3: flags=8051<UP,POINTOPOINT,RUNNING,MULTICAST> mtu 1280
        	inet 100.101.12.7 --> 100.101.12.7 netmask 0xffffffff
        ### route-get
           route to: default
        destination: default
               mask: default
            gateway: 192.168.0.1
          interface: en0
              flags: <UP,GATEWAY,DONE,STATIC,PRCLONING,GLOBAL>
        ### neigh
        ? (192.168.0.1) at 44:ad:b1:d6:39:59 on en0 ifscope [ethernet]
        ? (192.168.87.1) at 88:3d:24:aa:bb:cc on en1 ifscope [ethernet]
        ### trace
        traceroute to 1.1.1.1 (1.1.1.1), 4 hops max, 52 byte packets
         1  192.168.0.1  2.114 ms
         2  192.168.87.1  3.020 ms
         3  *
        ### tailscale-prefs
        """;

    [Fact]
    public void Parse_LinuxServer_KeepsTheLanInterfaceAndDropsEveryVirtualOne()
    {
        var report = DeviceNetworkReportParser.Parse(LinuxOutput);

        Assert.Equal("ubuntu-svr", report.Hostname);
        Assert.Equal(
            ["192.168.0.148", "fdc3:e1c9:2a8b:aa06:6a1d:efff:fe3c:d545"],
            report.Interfaces.Select(item => item.Address.ToString()));
        Assert.All(report.Interfaces, item => Assert.Equal("enp2s0", item.Name));
        Assert.Equal("68:1D:EF:3C:D5:45", report.Interfaces[0].Mac);
        Assert.Equal(24, report.PrimaryInterface?.PrefixLength);
    }

    [Fact]
    public void Parse_LinuxServer_ReadsGatewayRoutersAndAdvertisedRoutes()
    {
        var report = DeviceNetworkReportParser.Parse(LinuxOutput);

        Assert.Equal("192.168.0.1", report.Gateway?.ToString());
        Assert.Equal("enp2s0", report.GatewayInterface);
        Assert.Equal("44:AD:B1:D6:39:59", report.GatewayMac);
        Assert.Equal(["192.168.0.1", "192.168.87.1", "192.168.4.1"], report.RouterChain.Select(router => router.ToString()));
        Assert.Equal(["192.168.0.0/24"], report.AdvertisedRoutes);
        Assert.Equal("enp2s0 192.168.0.148/24 -> 192.168.0.1 -> 192.168.87.1 -> 192.168.4.1", report.DescribePath());
    }

    [Fact]
    public void Parse_CrlfOutput_ReadsTheSame()
    {
        var report = DeviceNetworkReportParser.Parse(LinuxOutput.ReplaceLineEndings("\r\n"));

        Assert.Equal("192.168.0.148", report.PrimaryInterface?.Address.ToString());
        Assert.Equal(3, report.RouterChain.Count);
    }

    [Fact]
    public void Parse_MacOs_ReadsIfconfigAndRouteGet()
    {
        var report = DeviceNetworkReportParser.Parse(MacOutput);

        Assert.Equal(
            ["192.168.0.154", "fdc3:e1c9:2a8b:aa06:1c8b:2b1f:a0c3:1e4d", "192.168.87.118"],
            report.Interfaces.Select(item => item.Address.ToString()));
        Assert.Equal("1C:F6:4C:51:76:D3", report.Interfaces[0].Mac);
        Assert.Equal("192.168.0.1", report.Gateway?.ToString());
        Assert.Equal("en0", report.PrimaryInterface?.Name);
        Assert.Equal(24, report.PrimaryInterface?.PrefixLength);
        Assert.Equal("44:AD:B1:D6:39:59", report.GatewayMac);
        Assert.Equal(["192.168.0.1", "192.168.87.1"], report.RouterChain.Select(router => router.ToString()));
        Assert.Empty(report.AdvertisedRoutes);
    }

    [Fact]
    public void Parse_GatewayIgnoringTraces_IsStillTheFirstRouter()
    {
        var report = DeviceNetworkReportParser.Parse("### os\nLinux\n### ip-route\ndefault via 10.0.5.1 dev eth0\n### trace\n 1  *\n 2  192.168.0.1  1.0 ms\n");

        Assert.Equal(["10.0.5.1", "192.168.0.1"], report.RouterChain.Select(router => router.ToString()));
    }

    [Fact]
    public async Task InspectAsync_TailscaleAddress_UsesKeysOnlyAndSendsTheScriptOnStandardInput()
    {
        string? arguments = null;
        string? input = null;
        var inspector = new SshNetworkInspector((file, args, _, _, stdin) =>
        {
            arguments = args;
            input = stdin;
            return Task.FromResult(new ProcessResult(true, 0, LinuxOutput, string.Empty, false));
        });

        var inspection = await inspector.InspectAsync("gagneet", "100.83.183.74", 22, hostIsAuthenticated: true);

        Assert.NotNull(inspection.Report);
        Assert.Contains("-o BatchMode=yes", arguments);
        Assert.Contains("-o StrictHostKeyChecking=accept-new", arguments);
        Assert.EndsWith("gagneet@100.83.183.74 sh -s", arguments);
        Assert.DoesNotContain("\r", input);
        Assert.Contains("### os", input);
    }

    [Fact]
    public async Task InspectAsync_LanAddress_NeverAcceptsAnUnknownHostKey()
    {
        string? arguments = null;
        var inspector = new SshNetworkInspector((_, args, _, _, _) =>
        {
            arguments = args;
            return Task.FromResult(new ProcessResult(true, 0, LinuxOutput, string.Empty, false));
        });

        await inspector.InspectAsync("gagneet", "192.168.0.148", 22, hostIsAuthenticated: false);

        Assert.Contains("-o StrictHostKeyChecking=yes", arguments);
    }

    [Theory]
    [InlineData("-oProxyCommand=calc", "192.168.0.148")]
    [InlineData("gagneet", "-oProxyCommand=calc")]
    [InlineData("gagneet", "host name")]
    [InlineData("gagneet%h", "192.168.0.148")]
    [InlineData("gagneet", "fe80::1%eth0")]
    public async Task InspectAsync_DestinationThatCouldBeReadAsAnOption_IsRefusedWithoutRunningSsh(string user, string host)
    {
        var ran = false;
        var inspector = new SshNetworkInspector((_, _, _, _, _) =>
        {
            ran = true;
            return Task.FromResult(new ProcessResult(true, 0, string.Empty, string.Empty, false));
        });

        var inspection = await inspector.InspectAsync(user, host, 22, hostIsAuthenticated: true);

        Assert.False(ran);
        Assert.Null(inspection.Report);
    }

    [Theory]
    [InlineData("gagneet@100.83.183.74: Permission denied (publickey).", "authorized_keys")]
    [InlineData("Host key verification failed.", "not trusted")]
    [InlineData("ssh: connect to host 100.83.183.74 port 22: Connection refused", "nothing accepts SSH")]
    public async Task InspectAsync_SshFailure_SaysWhatToDo(string standardError, string expected)
    {
        var inspector = new SshNetworkInspector((_, _, _, _, _) =>
            Task.FromResult(new ProcessResult(true, 255, string.Empty, standardError, false)));

        var inspection = await inspector.InspectAsync("gagneet", "100.83.183.74", 22, hostIsAuthenticated: true);

        Assert.Null(inspection.Report);
        Assert.Contains(expected, inspection.Failure);
    }

    [Fact]
    public void Explain_DeviceBehindAnotherRouter_NamesTheRoutersAndTheUnapprovedRoute()
    {
        var diagnosis = ReachabilityExplainer.Explain(ServerBehindRouter(), ProfileOn("192.168.87.50", "192.168.87.1"), route: null, serviceAnswered: false);

        Assert.Equal(ReachabilityCause.BehindRouter, diagnosis.Cause);
        Assert.True(diagnosis.ShortCause.Length <= 24);
        Assert.Contains("behind 192.168.0.1", diagnosis.Detail);
        Assert.Contains("hangs off 192.168.87.1", diagnosis.Detail);
        Assert.Contains("appears as 192.168.87.23", diagnosis.Detail);
        Assert.Contains("Approve the 192.168.0.0/24 route", diagnosis.Remedy);
        Assert.Contains("ubuntu-svr.tail7f7c1e.ts.net", diagnosis.Remedy);
    }

    [Fact]
    public void Explain_NoneOfTheDevicesRoutersIsLocal_ReportsSeparateBranches()
    {
        var diagnosis = ReachabilityExplainer.Explain(ServerBehindRouter(), ProfileOn("10.9.9.5", "10.9.9.1"), route: null, serviceAnswered: false);

        Assert.Equal(ReachabilityCause.DifferentSubnet, diagnosis.Cause);
        Assert.Equal("separate branches", diagnosis.ShortCause);
    }

    [Fact]
    public void Explain_DevicesGatewayIsOnThisNetwork_IsNotBehindARouter()
    {
        var diagnosis = ReachabilityExplainer.Explain(ServerBehindRouter(), ProfileOn("192.168.0.50", "192.168.0.1"), route: null, serviceAnswered: false);

        Assert.Equal(ReachabilityCause.ServiceNotAnswering, diagnosis.Cause);
    }

    [Fact]
    public void Explain_NatAddressWithoutAReport_StillSaysBehindARouter()
    {
        var location = ServerBehindRouter() with { NetworkReport = null, UnapprovedRoutes = [] };

        var diagnosis = ReachabilityExplainer.Explain(location, ProfileOn("192.168.87.50", "192.168.87.1"), route: null, serviceAnswered: false);

        Assert.Equal(ReachabilityCause.BehindRouter, diagnosis.Cause);
        Assert.Contains("192.168.87.23", diagnosis.Detail);
        Assert.Contains("connect over Tailscale", diagnosis.Remedy);
    }

    [Fact]
    public void Explain_ServiceAnswered_HasNothingToExplain()
    {
        var diagnosis = ReachabilityExplainer.Explain(ServerBehindRouter(), ProfileOn("192.168.87.50", "192.168.87.1"), route: null, serviceAnswered: true);

        Assert.False(diagnosis.HasDiagnosis);
    }

    [Fact]
    public void ParseStatus_ApprovedSubnetRoutes_AreReadPerPeer()
    {
        const string json = """
            {
              "BackendState": "Running",
              "Self": { "HostName": "laptop", "TailscaleIPs": ["100.66.86.87"] },
              "Peer": {
                "nodekey:1": { "HostName": "ubuntu-svr", "TailscaleIPs": ["100.83.183.74"], "Online": true, "PrimaryRoutes": ["192.168.0.0/24"] },
                "nodekey:2": { "HostName": "mac-mini", "TailscaleIPs": ["100.64.0.9"], "Online": true }
              }
            }
            """;

        var status = TailscaleStatusParser.Parse(json);

        Assert.Equal(["192.168.0.0/24"], status.Peers.Single(peer => peer.Name == "ubuntu-svr").PrimaryRoutes);
        Assert.Empty(status.Peers.Single(peer => peer.Name == "mac-mini").PrimaryRoutes);
    }

    private static DeviceLocation ServerBehindRouter() => new(
        "ubuntu-svr",
        "ubuntu-svr",
        IPAddress.Parse("192.168.0.148"),
        LocationSource.DeviceReported,
        LocationConfidence.High,
        [],
        [])
    {
        NetworkReport = DeviceNetworkReportParser.Parse(LinuxOutput),
        NatAddress = IPAddress.Parse("192.168.87.23"),
        UnapprovedRoutes = ["192.168.0.0/24"],
        TailscaleAddress = IPAddress.Parse("100.83.183.74"),
        TailscaleName = "ubuntu-svr.tail7f7c1e.ts.net"
    };

    private static LocalNetworkProfile ProfileOn(string address, string gateway) => new(
    [
        new LocalNetworkInterface(
            "eth0",
            "Test adapter",
            IPAddress.Parse(address),
            IPv4Network.FromAddressAndPrefix(IPAddress.Parse(address), 24),
            IPAddress.Parse(gateway))
    ]);
}
