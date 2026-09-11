using System.Net;
using LanInspector.Core.RemoteAccess;
using Xunit;

namespace LanInspector.Tests;

public sealed class TailscaleParsingTests
{
    // Trimmed from real `tailscale status --json` output. The peer carries the endpoint fields
    // that let the locator recover a LAN address after a DHCP change.
    private const string ConnectedJson = """
        {
          "Version": "1.56.1-t123abc",
          "BackendState": "Running",
          "Self": {
            "HostName": "my-laptop",
            "DNSName": "my-laptop.tail7f7c1e.ts.net.",
            "TailscaleIPs": ["100.64.0.1"]
          },
          "Peer": {
            "abc123": {
              "HostName": "ubuntu-svr",
              "DNSName": "ubuntu-svr.tail7f7c1e.ts.net.",
              "TailscaleIPs": ["100.83.183.74"],
              "Addrs": ["192.168.0.154:41641", "203.0.113.9:41641"],
              "CurAddr": "192.168.0.154:41641",
              "PeerAPIURL": ["http://192.168.0.154:37649"],
              "OS": "linux",
              "LastSeen": "2026-09-11T09:12:00Z",
              "Online": true
            },
            "def456": {
              "HostName": "other-device",
              "DNSName": "other-device.tail7f7c1e.ts.net.",
              "TailscaleIPs": ["100.64.0.3"],
              "Online": false
            }
          }
        }
        """;

    private const string NotRunningJson = """
        {
          "Version": "1.56.1",
          "BackendState": "Stopped"
        }
        """;

    [Fact]
    public void Parse_ConnectedJson_ReturnsConnected()
    {
        var status = TailscaleStatusParser.Parse(ConnectedJson);

        Assert.Equal(TailscaleConnectionState.Connected, status.State);
        Assert.Equal("my-laptop", status.LocalName);
        Assert.Equal("100.64.0.1", Assert.Single(status.LocalIps).ToString());
    }

    [Fact]
    public void Parse_ConnectedJson_ParsesPeers()
    {
        var status = TailscaleStatusParser.Parse(ConnectedJson);

        Assert.Equal(2, status.Peers.Count);
        var server = status.Peers.First(peer => peer.Name == "ubuntu-svr");
        Assert.True(server.IsOnline);
        Assert.Equal("100.83.183.74", server.TailscaleIps[0].ToString());
        Assert.Equal("ubuntu-svr.tail7f7c1e.ts.net", server.DnsName);
        Assert.Equal("linux", server.OperatingSystem);
    }

    [Fact]
    public void Parse_PeerWithDirectPath_ExposesCurrentAddress()
    {
        var server = TailscaleStatusParser.Parse(ConnectedJson).Peers.First(peer => peer.Name == "ubuntu-svr");

        Assert.NotNull(server.CurrentAddress);
        Assert.Equal("192.168.0.154", server.CurrentAddress!.Address.ToString());
        Assert.Equal(41641, server.CurrentAddress.Port);
    }

    [Fact]
    public void LanAddressCandidates_KeepsPrivateAddressesAndDropsPublicOnes()
    {
        var server = TailscaleStatusParser.Parse(ConnectedJson).Peers.First(peer => peer.Name == "ubuntu-svr");

        var candidates = server.LanAddressCandidates.Select(address => address.ToString()).ToArray();

        Assert.Equal(["192.168.0.154"], candidates);
    }

    [Fact]
    public void Parse_PeerWithoutEndpoints_HasNoLanCandidates()
    {
        var peer = TailscaleStatusParser.Parse(ConnectedJson).Peers.First(p => p.Name == "other-device");

        Assert.Empty(peer.Endpoints);
        Assert.Null(peer.CurrentAddress);
        Assert.Empty(peer.LanAddressCandidates);
    }

    [Fact]
    public void Parse_NotRunningJson_ReturnsInstalledNotConnected()
    {
        var status = TailscaleStatusParser.Parse(NotRunningJson);

        Assert.Equal(TailscaleConnectionState.InstalledNotConnected, status.State);
        Assert.Empty(status.Peers);
    }

    [Fact]
    public void Parse_Garbage_DoesNotThrow()
    {
        var status = TailscaleStatusParser.Parse("not json at all");

        Assert.Equal(TailscaleConnectionState.InstalledNotConnected, status.State);
    }

    [Theory]
    [InlineData("ubuntu-svr")]
    [InlineData("UBUNTU-SVR")]
    [InlineData("ubuntu-svr.tail7f7c1e.ts.net")]
    [InlineData("ubuntu-svr.tail7f7c1e.ts.net.")]
    public void FindPeerByName_MatchesShortAndQualifiedNames(string name)
    {
        var status = TailscaleStatusParser.Parse(ConnectedJson);

        Assert.Equal("ubuntu-svr", status.FindPeerByName([name])?.Name);
    }

    [Fact]
    public void FindPeerByName_UnknownName_ReturnsNull()
    {
        var status = TailscaleStatusParser.Parse(ConnectedJson);

        Assert.Null(status.FindPeerByName(["no-such-host"]));
    }

    [Fact]
    public void TryParseDirectEndpoint_DirectReply_ReturnsLanEndpoint()
    {
        const string output = """
            pong from ubuntu-svr (100.83.183.74) via 192.168.0.154:41641 in 3ms
            """;

        var endpoint = TailscalePingParser.TryParseDirectEndpoint(output);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.154"), 41641), endpoint);
    }

    [Fact]
    public void TryParseDirectEndpoint_SkipsRelayedRepliesUntilDirectOneArrives()
    {
        const string output = """
            pong from ubuntu-svr (100.83.183.74) via DERP(syd) in 42ms
            pong from ubuntu-svr (100.83.183.74) via DERP(syd) in 41ms
            pong from ubuntu-svr (100.83.183.74) via 192.168.0.154:41641 in 2ms
            """;

        Assert.Equal("192.168.0.154", TailscalePingParser.TryParseDirectEndpoint(output)?.Address.ToString());
        Assert.False(TailscalePingParser.IsRelayedOnly(output));
    }

    [Fact]
    public void TryParseDirectEndpoint_RelayedOnly_ReturnsNull()
    {
        const string output = "pong from ubuntu-svr (100.83.183.74) via DERP(syd) in 42ms";

        Assert.Null(TailscalePingParser.TryParseDirectEndpoint(output));
        Assert.True(TailscalePingParser.IsRelayedOnly(output));
    }

    [Fact]
    public void TryParseDirectEndpoint_NoReply_ReturnsNull()
    {
        Assert.Null(TailscalePingParser.TryParseDirectEndpoint("no matching peer"));
        Assert.Null(TailscalePingParser.TryParseDirectEndpoint(null));
        Assert.False(TailscalePingParser.IsRelayedOnly("no matching peer"));
    }
}
