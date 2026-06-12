using System.Net;
using LanInspector.Core.Network;

namespace LanInspector.Core.Diagnostics;

public sealed class ReachabilityClassifier
{
    private readonly ILocalNetworkProfileProvider _profileProvider;

    public ReachabilityClassifier(ILocalNetworkProfileProvider profileProvider)
    {
        _profileProvider = profileProvider;
    }

    public LocalNetworkProfile GetCurrentProfile()
    {
        return _profileProvider.GetCurrentProfile();
    }

    public DeviceNetworkClassification Classify(IPAddress address, bool hasOpenTcpPort, RouteDecision? routeDecision = null)
    {
        var profile = _profileProvider.GetCurrentProfile();
        var localInterface = profile.FindLocalInterface(address);
        if (localInterface is not null)
        {
            return new DeviceNetworkClassification(
                localInterface.Network.ToString(),
                ReachabilityKind.LocalLayer2,
                localInterface.GatewayAddress?.ToString() ?? string.Empty,
                $"Local L2 on {localInterface.Name} ({localInterface.Network})");
        }

        if (routeDecision?.NextHop is not null)
        {
            var reachability = hasOpenTcpPort ? ReachabilityKind.Routed : ReachabilityKind.RouteExistsServiceUnavailable;
            return new DeviceNetworkClassification(
                InferSegment(address),
                reachability,
                routeDecision.NextHop.ToString(),
                routeDecision.RouteSummary);
        }

        if (hasOpenTcpPort)
        {
            return new DeviceNetworkClassification(
                InferSegment(address),
                ReachabilityKind.ReachableTcpOnly,
                profile.DefaultGateway?.ToString() ?? string.Empty,
                "TCP reachable, route details unavailable");
        }

        return new DeviceNetworkClassification(InferSegment(address), ReachabilityKind.Unknown, string.Empty, "No route or TCP evidence yet");
    }

    private static string InferSegment(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24" : string.Empty;
    }
}

public sealed record DeviceNetworkClassification(
    string Segment,
    ReachabilityKind Reachability,
    string Gateway,
    string RouteSummary);
