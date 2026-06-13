using System.Net;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;

namespace LanInspector.Core.Visibility;

public sealed class VisibilityExplanationService : IVisibilityExplanationService
{
    private readonly IRouteDiagnosticsService _routeDiagnostics;
    private readonly ILocalNetworkProfileProvider _profileProvider;
    private readonly KnownDevicesConfiguration _knownDevices;
    private readonly ITailscaleService _tailscale;

    public VisibilityExplanationService(
        IRouteDiagnosticsService routeDiagnostics,
        ILocalNetworkProfileProvider profileProvider,
        KnownDevicesConfiguration knownDevices,
        ITailscaleService tailscale)
    {
        _routeDiagnostics = routeDiagnostics;
        _profileProvider = profileProvider;
        _knownDevices = knownDevices;
        _tailscale = tailscale;
    }

    public async Task<VisibilityExplanation> ExplainAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var profile = _profileProvider.GetCurrentProfile();
        var tailscaleStatus = await _tailscale.GetStatusAsync(cancellationToken);
        var route = await _routeDiagnostics.GetRouteToAsync(target, cancellationToken);

        var label = ResolveLabel(target);
        var canSee = new List<string>();
        var cannotSee = new List<string>();
        var why = new List<string>();
        var howToImprove = new List<string>();

        var localIface = profile.FindLocalInterface(target);
        bool isSameSubnet = localIface is not null;
        bool isCgnat = RouteHelpers.IsCgnatOrTailscale(route.NextHop ?? target);
        bool isPublic = RouteHelpers.IsPublicInternet(target);
        var misconfiguration = RouteHelpers.DetectMisconfiguration(route);

        VisibilityResult result;

        if (isSameSubnet)
        {
            result = VisibilityResult.Visible;
            canSee.Add($"Target {target} is on the local subnet {localIface!.Network}");
            canSee.Add("Layer 2 (ARP) reachability expected");
            why.Add($"You are on the same subnet ({localIface.Network}) as the target");
            why.Add($"Interface: {localIface.Name} ({localIface.Address})");
        }
        else if (misconfiguration is not null)
        {
            result = VisibilityResult.NotVisible;
            cannotSee.Add($"Target {target} is not reachable from current subnet");
            why.Add(misconfiguration.UserFriendly);
            why.Add(misconfiguration.Technical);
            if (misconfiguration.Kind == RouteMisconfigurationKind.EgressCgnat)
            {
                howToImprove.Add("Run a Tailscale subnet router on a machine that IS on the target subnet");
                howToImprove.Add("Advertise the target subnet with: sudo tailscale up --advertise-routes=<subnet>");
                if (tailscaleStatus.State == TailscaleConnectionState.Connected)
                    howToImprove.Add("Alternatively: connect to the network that contains this subnet");
            }
        }
        else if (isPublic)
        {
            result = VisibilityResult.ProbablyNotVisible;
            cannotSee.Add($"Target {target} is a public internet address");
            why.Add("Public IP ranges are not directly accessible on local networks");
            howToImprove.Add("Ensure the device has a public-facing address or use a VPN/tunnel");
        }
        else if (route.Reachability == ReachabilityKind.Unreachable)
        {
            result = VisibilityResult.NotVisible;
            cannotSee.Add($"No route found for {target}");
            why.Add($"Route lookup returned: {route.RouteSummary}");
            howToImprove.Add("Check your network configuration and routing tables");
        }
        else
        {
            result = VisibilityResult.ProbablyVisible;
            canSee.Add($"Route exists to {target} via {route.NextHop?.ToString() ?? "unknown"}");
            why.Add($"Routed via: {route.RouteSummary}");
            why.Add($"Interface: {route.InterfaceAlias}");
        }

        // Check Tailscale overlay
        if (tailscaleStatus.State == TailscaleConnectionState.Connected)
        {
            var tsPeer = tailscaleStatus.Peers.FirstOrDefault(p =>
                p.TailscaleIps.Any(ip => ip.Equals(target)));
            if (tsPeer is not null)
            {
                canSee.Add($"Tailscale peer '{tsPeer.Name}' matches this IP");
                canSee.Add($"Tailscale overlay provides reachability regardless of local routing");
                result = VisibilityResult.Visible;
            }
        }

        var summary = result switch
        {
            VisibilityResult.Visible => $"You can reach {label} ({target}).",
            VisibilityResult.ProbablyVisible => $"You probably can reach {label} ({target}) via routing.",
            VisibilityResult.ProbablyNotVisible => $"You probably cannot reach {label} ({target}).",
            VisibilityResult.NotVisible => $"You cannot reach {label} ({target}) from your current network.",
            _ => $"Visibility to {label} ({target}) is unknown."
        };

        return new VisibilityExplanation(target, label, result, summary, canSee, cannotSee, why, howToImprove);
    }

    public async Task<IReadOnlyList<VisibilityExplanation>> ExplainAllKnownAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<VisibilityExplanation>();
        foreach (var device in _knownDevices.KnownDevices)
        {
            foreach (var ipStr in device.KnownIps)
            {
                if (IPAddress.TryParse(ipStr, out var ip))
                {
                    var explanation = await ExplainAsync(ip, cancellationToken);
                    results.Add(explanation);
                }
            }
        }
        return results;
    }

    private string ResolveLabel(IPAddress target)
    {
        var known = _knownDevices.KnownDevices
            .FirstOrDefault(d => d.KnownIps.Any(ip => string.Equals(ip, target.ToString(), StringComparison.OrdinalIgnoreCase)));
        return known?.DisplayName ?? target.ToString();
    }
}
