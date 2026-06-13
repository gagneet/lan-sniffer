using System.Net;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Network;
using LanInspector.Core.Scanning;

namespace LanInspector.Core.RemoteAccess;

public sealed class RemoteAccessRecommendationEngine
{
    private readonly ILocalNetworkProfileProvider _networkProfileProvider;
    private readonly IRouteDiagnosticsService _routeDiagnostics;
    private readonly ITailscaleService _tailscaleService;
    private readonly PortScanner _portScanner;

    public RemoteAccessRecommendationEngine(
        ILocalNetworkProfileProvider networkProfileProvider,
        IRouteDiagnosticsService routeDiagnostics,
        ITailscaleService tailscaleService,
        PortScanner portScanner)
    {
        _networkProfileProvider = networkProfileProvider;
        _routeDiagnostics = routeDiagnostics;
        _tailscaleService = tailscaleService;
        _portScanner = portScanner;
    }

    public async Task<RemoteAccessRecommendation> RecommendAsync(
        KnownDeviceDefinition device,
        CancellationToken cancellationToken = default)
    {
        var ssh = device.Ssh;
        if (ssh?.Enabled != true || string.IsNullOrWhiteSpace(ssh.User))
        {
            return new RemoteAccessRecommendation(
                device.Id,
                device.DisplayName,
                [],
                null,
                "No SSH profile configured for this device.");
        }

        var candidates = new List<ConnectionCandidate>();
        string? cgnatWarning = null;
        string? bestSubnetRouteCmd = null;

        // Test direct LAN IPs
        foreach (var ipStr in device.KnownIps)
        {
            if (!IPAddress.TryParse(ipStr, out var ip))
            {
                continue;
            }

            var portResult = await _routeDiagnostics.TestPortAsync(ip, ssh.Port, "SSH", cancellationToken);
            if (portResult.IsOpen)
            {
                var cmd = SshCommandGenerator.Generate(ssh.User, ipStr, ssh.Port);
                candidates.Add(new ConnectionCandidate(
                    ConnectionMethod.DirectLanIp,
                    ipStr,
                    ssh.Port,
                    cmd,
                    $"Direct LAN connection to {ipStr}"));
            }
            else
            {
                var route = await _routeDiagnostics.GetRouteToAsync(ip, cancellationToken);
                var trace = await _routeDiagnostics.TraceRouteAsync(ip, cancellationToken);
                var misconfig = RouteHelpers.DetectMisconfiguration(route, trace);
                if (misconfig is not null)
                {
                    cgnatWarning = misconfig.UserFriendly;
                }
            }
        }

        // Check Tailscale
        var tailscale = await _tailscaleService.GetStatusAsync(cancellationToken);
        if (tailscale.State == TailscaleConnectionState.Connected)
        {
            var matchedPeer = FindTailscalePeer(tailscale, device);
            if (matchedPeer is not null)
            {
                foreach (var tailscaleIp in matchedPeer.TailscaleIps)
                {
                    var ipStr = tailscaleIp.ToString();
                    var cmd = SshCommandGenerator.Generate(ssh.User, ipStr, ssh.Port);
                    candidates.Add(new ConnectionCandidate(
                        ConnectionMethod.TailscaleIp,
                        ipStr,
                        ssh.Port,
                        cmd,
                        $"Tailscale IP ({matchedPeer.Name})"));
                }

                if (!string.IsNullOrWhiteSpace(matchedPeer.Name))
                {
                    var cmd = SshCommandGenerator.Generate(ssh.User, matchedPeer.Name, ssh.Port);
                    candidates.Add(new ConnectionCandidate(
                        ConnectionMethod.TailscaleName,
                        matchedPeer.Name,
                        ssh.Port,
                        cmd,
                        $"Tailscale hostname ({matchedPeer.Name})"));
                }

                if (cgnatWarning is not null)
                {
                    bestSubnetRouteCmd = SubnetRouteAssistant.BuildCommand(device);
                }
            }
        }
        else if (cgnatWarning is not null)
        {
            bestSubnetRouteCmd = SubnetRouteAssistant.BuildCommand(device);
        }

        var best = candidates.FirstOrDefault();
        var summary = best is not null
            ? $"Best method: {best.Method} via {best.Target}"
            : "No reachable path found. Consider Tailscale or connecting to the same LAN.";

        return new RemoteAccessRecommendation(
            device.Id,
            device.DisplayName,
            candidates,
            best,
            summary,
            bestSubnetRouteCmd,
            cgnatWarning);
    }

    private static TailscaleDevice? FindTailscalePeer(TailscaleStatus status, KnownDeviceDefinition device)
    {
        return status.Peers.FirstOrDefault(peer =>
            device.KnownTailscaleNames.Any(name =>
                string.Equals(name, peer.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, peer.DnsName, StringComparison.OrdinalIgnoreCase)) ||
            device.KnownIps.Any(ip => peer.TailscaleIps.Any(tip =>
                string.Equals(ip, tip.ToString(), StringComparison.OrdinalIgnoreCase))));
    }
}
