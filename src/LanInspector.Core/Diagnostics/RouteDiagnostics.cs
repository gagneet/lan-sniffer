using System.Net;

namespace LanInspector.Core.Diagnostics;

public enum ReachabilityKind
{
    Unknown,
    LocalLayer2,
    Routed,
    ReachableTcpOnly,
    RouteExistsServiceUnavailable,
    Unreachable
}

public sealed record RouteDecision(
    IPAddress Target,
    IPAddress? SourceAddress,
    IPAddress? NextHop,
    string InterfaceAlias,
    string RouteSummary,
    ReachabilityKind Reachability);

public sealed record PortReachability(IPAddress Target, int Port, bool IsOpen, string ServiceName);

public sealed record TraceRouteResult(IPAddress Target, IReadOnlyList<string> Hops, string Summary);

public interface IRouteDiagnosticsService
{
    Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken cancellationToken = default);

    Task<PortReachability> TestPortAsync(IPAddress target, int port, string serviceName, CancellationToken cancellationToken = default);

    Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken cancellationToken = default);
}
