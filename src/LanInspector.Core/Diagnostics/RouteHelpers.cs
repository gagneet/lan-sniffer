using System.Net;
using LanInspector.Core.Network;

namespace LanInspector.Core.Diagnostics;

public static class RouteHelpers
{
    private static readonly IPv4Network Rfc1918_10 = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("10.0.0.0"), 8);
    private static readonly IPv4Network Rfc1918_172 = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("172.16.0.0"), 12);
    private static readonly IPv4Network Rfc1918_192 = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("192.168.0.0"), 16);
    private static readonly IPv4Network CgnatRange = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("100.64.0.0"), 10);
    private static readonly IPv4Network LoopbackRange = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("127.0.0.0"), 8);

    public static bool IsRfc1918(IPAddress address) =>
        Rfc1918_10.Contains(address) || Rfc1918_172.Contains(address) || Rfc1918_192.Contains(address);

    public static bool IsCgnatOrTailscale(IPAddress address) => CgnatRange.Contains(address);

    public static bool IsLoopback(IPAddress address) => LoopbackRange.Contains(address);

    public static bool IsPublicInternet(IPAddress address) =>
        !IsRfc1918(address) && !IsCgnatOrTailscale(address) && !IsLoopback(address);

    public static RouteMisconfiguration? DetectMisconfiguration(RouteDecision route, TraceRouteResult? trace = null)
    {
        if (!IsRfc1918(route.Target))
        {
            return null;
        }

        if (route.NextHop is not null && IsCgnatOrTailscale(route.NextHop) && !IsRfc1918(route.NextHop))
        {
            return new RouteMisconfiguration(
                RouteMisconfigurationKind.EgressCgnat,
                $"The router is sending traffic for private address {route.Target} upstream via {route.NextHop} (CGNAT/shared address space).",
                "This router does not know how to reach the target LAN subnet and is sending the packet upstream.");
        }

        if (trace is not null)
        {
            foreach (var hop in trace.Hops.Skip(1))
            {
                var hopIp = ExtractIpFromHop(hop);
                if (hopIp is null)
                {
                    continue;
                }

                if (IsCgnatOrTailscale(hopIp) && !IsRfc1918(hopIp))
                {
                    return new RouteMisconfiguration(
                        RouteMisconfigurationKind.EgressCgnat,
                        $"Trace to {route.Target} exits via CGNAT address {hopIp}.",
                        "This router does not know how to reach the target LAN subnet and is sending the packet upstream.");
                }

                if (IsPublicInternet(hopIp))
                {
                    return new RouteMisconfiguration(
                        RouteMisconfigurationKind.EgressPublicInternet,
                        $"Trace to private address {route.Target} exits to public internet via {hopIp}.",
                        "The local router is routing this private destination to the public internet. Normal inbound port forwarding may fail. Consider Tailscale.");
                }
            }
        }

        return null;
    }

    private static IPAddress? ExtractIpFromHop(string hop)
    {
        var parts = hop.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (IPAddress.TryParse(part.TrimEnd(')', '(', '[', ']'), out var address))
            {
                return address;
            }
        }

        return null;
    }
}

public enum RouteMisconfigurationKind
{
    EgressCgnat,
    EgressPublicInternet
}

public sealed record RouteMisconfiguration(
    RouteMisconfigurationKind Kind,
    string Technical,
    string UserFriendly);
