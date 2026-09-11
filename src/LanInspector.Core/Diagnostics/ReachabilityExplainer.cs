using System.Net;
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
    RoutedUpstream
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
