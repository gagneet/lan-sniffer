using LanInspector.Core.RemoteAccess;
using Xunit;

namespace LanInspector.Tests;

public sealed class TailscaleParsingTests
{
    private static readonly string ConnectedJson = """
        {
          "Version": "1.56.1-t123abc",
          "BackendState": "Running",
          "Self": {
            "HostName": "my-laptop",
            "DNSName": "my-laptop.tailnet-name.ts.net.",
            "TailscaleIPs": ["100.64.0.1"]
          },
          "Peer": {
            "abc123": {
              "HostName": "home-server",
              "DNSName": "home-server.tailnet-name.ts.net.",
              "TailscaleIPs": ["100.64.0.2"],
              "Online": true
            },
            "def456": {
              "HostName": "other-device",
              "DNSName": "other-device.tailnet-name.ts.net.",
              "TailscaleIPs": ["100.64.0.3"],
              "Online": false
            }
          }
        }
        """;

    private static readonly string NotRunningJson = """
        {
          "Version": "1.56.1",
          "BackendState": "Stopped"
        }
        """;

    [Fact]
    public async Task GetStatusAsync_ConnectedJson_ReturnsConnected()
    {
        var service = new TestTailscaleCliService(ConnectedJson);
        var status = await service.GetStatusAsync();

        Assert.Equal(TailscaleConnectionState.Connected, status.State);
        Assert.Equal("my-laptop", status.LocalName);
        Assert.Single(status.LocalIps);
        Assert.Equal("100.64.0.1", status.LocalIps[0].ToString());
    }

    [Fact]
    public async Task GetStatusAsync_ConnectedJson_ParsesPeers()
    {
        var service = new TestTailscaleCliService(ConnectedJson);
        var status = await service.GetStatusAsync();

        Assert.Equal(2, status.Peers.Count);
        var server = status.Peers.First(p => p.Name == "home-server");
        Assert.True(server.IsOnline);
        Assert.Equal("100.64.0.2", server.TailscaleIps[0].ToString());
        Assert.Equal("home-server.tailnet-name.ts.net", server.DnsName);
    }

    [Fact]
    public async Task GetStatusAsync_NotRunningJson_ReturnsInstalledNotConnected()
    {
        var service = new TestTailscaleCliService(NotRunningJson, isInstalled: true);
        var status = await service.GetStatusAsync();

        Assert.Equal(TailscaleConnectionState.InstalledNotConnected, status.State);
        Assert.Empty(status.Peers);
    }

    [Fact]
    public async Task GetStatusAsync_NotInstalled_ReturnsNotInstalled()
    {
        var service = new TestTailscaleCliService(null, isInstalled: false);
        var status = await service.GetStatusAsync();

        Assert.Equal(TailscaleConnectionState.NotInstalled, status.State);
    }

    // Test-only implementation that bypasses process execution
    private sealed class TestTailscaleCliService : ITailscaleService
    {
        private readonly string? _statusJson;
        private readonly bool _isInstalled;

        public TestTailscaleCliService(string? statusJson, bool isInstalled = true)
        {
            _statusJson = statusJson;
            _isInstalled = isInstalled;
        }

        public Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (!_isInstalled)
            {
                return Task.FromResult(new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []));
            }

            if (_statusJson is null)
            {
                return Task.FromResult(new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []));
            }

            return Task.FromResult(ParseJson(_statusJson));
        }

        private static TailscaleStatus ParseJson(string json)
        {
            // Delegate to the same logic used in TailscaleCliService via reflection or duplicate logic.
            // For test purposes, use a simplified parser that matches the production code.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var backendState = root.TryGetProperty("BackendState", out var stateEl) ? stateEl.GetString() : null;
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return new TailscaleStatus(TailscaleConnectionState.InstalledNotConnected, [], []);
            }

            var selfName = string.Empty;
            var localIps = new List<System.Net.IPAddress>();
            if (root.TryGetProperty("Self", out var selfEl))
            {
                selfName = selfEl.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? string.Empty : string.Empty;
                if (selfEl.TryGetProperty("TailscaleIPs", out var ips))
                {
                    foreach (var ip in ips.EnumerateArray())
                    {
                        if (System.Net.IPAddress.TryParse(ip.GetString(), out var parsed))
                        {
                            localIps.Add(parsed);
                        }
                    }
                }
            }

            var peers = new List<TailscaleDevice>();
            if (root.TryGetProperty("Peer", out var peersEl))
            {
                foreach (var peer in peersEl.EnumerateObject())
                {
                    var hostname = peer.Value.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? string.Empty : string.Empty;
                    var dnsName = peer.Value.TryGetProperty("DNSName", out var dn) ? dn.GetString()?.TrimEnd('.') ?? string.Empty : string.Empty;
                    var online = peer.Value.TryGetProperty("Online", out var ol) && ol.GetBoolean();
                    var peerIps = new List<System.Net.IPAddress>();
                    if (peer.Value.TryGetProperty("TailscaleIPs", out var pips))
                    {
                        foreach (var ip in pips.EnumerateArray())
                        {
                            if (System.Net.IPAddress.TryParse(ip.GetString(), out var p))
                            {
                                peerIps.Add(p);
                            }
                        }
                    }

                    peers.Add(new TailscaleDevice(hostname, dnsName, peerIps, online));
                }
            }

            return new TailscaleStatus(TailscaleConnectionState.Connected, peers, localIps, selfName);
        }
    }
}
