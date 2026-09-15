using System.Net;
using LanInspector.Core.Locator;
using LanInspector.Core.Network;

namespace LanInspector.Core.Diagnostics;

/// <summary>
/// Why a device could not be reached, split into a label short enough to sit in a table row and
/// the full explanation behind it.
/// </summary>
public enum ReachabilityCause
{
    /// <summary>Nothing to explain — the device answered.</summary>
    None,

    /// <summary>The target is on a subnet this machine has no path into.</summary>
    DifferentSubnet,

    /// <summary>The target is on this machine's own subnet, so the route is not the problem.</summary>
    ServiceNotAnswering,

    /// <summary>Traffic for a private address is being sent upstream, typically into CGNAT.</summary>
    RoutedUpstream,

    /// <summary>The target sits behind a NAT router of its own, which lets nothing in from this side.</summary>
    BehindRouter
}

/// <param name="ShortCause">Two or three words, for a table row.</param>
/// <param name="Detail">The full explanation, for an expanded view or the CLI.</param>
/// <param name="Remedy">What to do about it, when there is a concrete answer.</param>
public sealed record ReachabilityDiagnosis(
    ReachabilityCause Cause,
    string ShortCause,
    string Detail,
    string Remedy = "")
{
    public static ReachabilityDiagnosis None { get; } = new(ReachabilityCause.None, string.Empty, string.Empty);

    public bool HasDiagnosis => Cause != ReachabilityCause.None;
}

/// <summary>
/// Turns a failed reachability check into something that says what to do about it.
/// </summary>
/// <remarks>
/// "Not reachable" on its own sends people looking for a fault on the target machine, when the
/// usual cause on a multi-router home network is that the machine running this application sits on
/// a different subnet with no route to the target. That is a property of the network between them,
/// not of either device, and the two cases need opposite responses.
/// <para>
/// The result is split because the two audiences differ: a table row has space for "different
/// subnet" and nothing more, while the CLI and an expanded row want the whole account. Returning
/// one long string forced the caller to show all of it or none.
/// </para>
/// </remarks>
public static class ReachabilityExplainer
{
    public static ReachabilityDiagnosis Explain(
        IPAddress target,
        LocalNetworkProfile profile,
        RouteDecision? route,
        bool serviceAnswered,
        IPAddress? tailscaleAddress = null,
        string? tailscaleName = null)
    {
        if (serviceAnswered)
        {
            return ReachabilityDiagnosis.None;
        }

        var overlay = DescribeOverlay(tailscaleAddress, tailscaleName);
        var localInterface = profile.FindLocalInterface(target);

        if (localInterface is not null)
        {
            // Same segment: the network is not the problem, so the service or a host firewall is.
            return new ReachabilityDiagnosis(
                ReachabilityCause.ServiceNotAnswering,
                "no answer on port",
                $"{target} is on this machine's own subnet ({localInterface.Network}), so the route is fine — " +
                $"nothing answered on the probed port." + overlay,
                "Check the service is running and the host firewall allows it.");
        }

        var misconfiguration = route is null ? null : RouteHelpers.DetectMisconfiguration(route);
        if (misconfiguration is not null)
        {
            return new ReachabilityDiagnosis(
                ReachabilityCause.RoutedUpstream,
                "routed upstream",
                misconfiguration.UserFriendly + overlay,
                "Run a Tailscale subnet router on a machine that is on the target's network.");
        }

        var localNetworks = profile.Interfaces.Count == 0
            ? "no active IPv4 interface"
            : string.Join(", ", profile.Interfaces.Select(item => item.Network.ToString()).Distinct());

        return new ReachabilityDiagnosis(
            ReachabilityCause.DifferentSubnet,
            "different subnet",
            $"This machine is on {localNetworks}; {target} is on {DescribeSubnet(target)}. " +
            "Those are different subnets, and the router between them does not carry traffic from this side to that one." +
            overlay,
            "Connect to the same network as the target, add a route, or reach it over Tailscale.");
    }

    /// <summary>
    /// Explains a located device, using what the locator learned beyond an address: the device's
    /// own account of its routers, and the NAT router its traffic lands on.
    /// </summary>
    public static ReachabilityDiagnosis Explain(
        DeviceLocation location,
        LocalNetworkProfile profile,
        RouteDecision? route,
        bool serviceAnswered)
    {
        if (serviceAnswered)
        {
            return ReachabilityDiagnosis.None;
        }

        if (location.NetworkReport is { } report && ExplainFromReport(location, report, profile) is { } fromReport)
        {
            return fromReport;
        }

        // A device whose own gateway is on this network has no router in between, whatever else
        // suggested one; its report outranks the guess.
        var gatewayIsLocal = location.NetworkReport?.RouterChain.FirstOrDefault() is { } gateway
            && profile.FindLocalInterface(gateway) is not null;

        if (location.NatAddress is { } nat && !gatewayIsLocal)
        {
            return new ReachabilityDiagnosis(
                ReachabilityCause.BehindRouter,
                "behind another router",
                $"Traffic for {location.DisplayName} ends at {nat}, a router it sits behind. Its own address is on that router's " +
                "inside network, and a NAT router lets nothing in from outside unless a port is forwarded." +
                DescribeOverlay(location.TailscaleAddress, location.TailscaleName),
                BuildRemedy(location, network: null));
        }

        return location.CurrentAddress is null
            ? ReachabilityDiagnosis.None
            : Explain(location.CurrentAddress, profile, route, serviceAnswered, location.TailscaleAddress, location.TailscaleName);
    }

    /// <summary>
    /// Walks the device's routers outward until one is on a network this machine is on. Every
    /// router before that one stands between the two machines.
    /// </summary>
    private static ReachabilityDiagnosis? ExplainFromReport(DeviceLocation location, DeviceNetworkReport report, LocalNetworkProfile profile)
    {
        var chain = report.RouterChain;
        var primary = report.PrimaryInterface;
        if (chain.Count == 0 || primary is null)
        {
            return null;
        }

        var joinIndex = -1;
        for (var index = 0; index < chain.Count; index++)
        {
            if (profile.FindLocalInterface(chain[index]) is not null)
            {
                joinIndex = index;
                break;
            }
        }

        // The device's own gateway is on this network, so no router stands in between.
        if (joinIndex == 0)
        {
            return null;
        }

        var name = location.DisplayName;
        var network = primary.Network?.ToString() ?? $"{primary.Address}/{primary.PrefixLength}";
        var vendor = string.IsNullOrWhiteSpace(report.GatewayVendor) ? string.Empty : $" ({report.GatewayVendor})";
        var overlay = DescribeOverlay(location.TailscaleAddress, location.TailscaleName);

        if (joinIndex > 0)
        {
            var local = profile.FindLocalInterface(chain[joinIndex])!;
            var inBetween = string.Join(" and ", chain.Take(joinIndex));
            var outside = location.NatAddress is null ? string.Empty : $", where it appears as {location.NatAddress}";

            return new ReachabilityDiagnosis(
                ReachabilityCause.BehindRouter,
                "behind another router",
                $"{name} is at {primary.Address} on {network}, behind {inBetween}{vendor}. That router hangs off {chain[joinIndex]} " +
                $"on this machine's network {local.Network}{outside}. A NAT router lets nothing in from outside unless a port is " +
                $"forwarded, so {primary.Address} cannot be reached from here." + overlay,
                BuildRemedy(location, network));
        }

        var localNetworks = string.Join(", ", profile.Interfaces.Select(item => item.Network.ToString()).Distinct());
        return new ReachabilityDiagnosis(
            ReachabilityCause.DifferentSubnet,
            "separate branches",
            $"{name} is at {primary.Address} on {network}, behind {string.Join(" then ", chain)}{vendor}. None of those routers is on " +
            $"this machine's network ({(localNetworks.Length == 0 ? "none" : localNetworks)}), so the two sit on separate branches." + overlay,
            BuildRemedy(location, network));
    }

    private static string BuildRemedy(DeviceLocation location, string? network)
    {
        var steps = new List<string>();
        var name = location.DisplayName;
        var tailscaleName = location.TailscaleName ?? location.TailscaleAddress?.ToString();
        var advertised = location.NetworkReport?.AdvertisedRoutes ?? [];

        if (location.UnapprovedRoutes.Count > 0)
        {
            steps.Add($"Approve the {string.Join(", ", location.UnapprovedRoutes)} route in the Tailscale admin console " +
                      $"(Machines → {tailscaleName ?? name} → Edit route settings); the LAN address then works from any device on the tailnet.");
        }
        else if (advertised.Count > 0)
        {
            steps.Add("Its subnet route is approved; a Linux client also needs 'sudo tailscale set --accept-routes' to use it.");
        }
        else if (network is not null && location.TailscaleAddress is not null)
        {
            steps.Add($"Run 'sudo tailscale set --advertise-routes={network}' on {name}, then approve the route in the Tailscale admin console.");
        }

        steps.Add(tailscaleName is not null
            ? $"Meanwhile, connect over Tailscale: {tailscaleName}."
            : $"Install Tailscale on {name} to reach it from anywhere.");

        if (location.NatAddress is not null)
        {
            steps.Add($"Alternatively, forward a port on that router and connect to {location.NatAddress}, or move {name} onto this network.");
        }

        return string.Join(" ", steps);
    }

    /// <summary>The detail and remedy as one sentence, for output with no room for structure.</summary>
    public static string ToSingleLine(this ReachabilityDiagnosis diagnosis) =>
        string.IsNullOrEmpty(diagnosis.Remedy)
            ? diagnosis.Detail
            : $"{diagnosis.Detail} {diagnosis.Remedy}";

    private static string DescribeOverlay(IPAddress? tailscaleAddress, string? tailscaleName)
    {
        if (tailscaleAddress is null)
        {
            return string.Empty;
        }

        var via = string.IsNullOrWhiteSpace(tailscaleName) ? tailscaleAddress.ToString() : tailscaleName;
        return $" Tailscale reaches it now at {via}.";
    }

    private static string DescribeSubnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24" : address.ToString();
    }
}
