using System.Net;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Scanning;

namespace LanInspector.Platform.MacOS;

public sealed class MacOsRouteDiagnosticsService : IRouteDiagnosticsService
{
    public async Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var output = await ProcessHelper.RunAsync("route", $"-n get {target}", TimeSpan.FromSeconds(5), cancellationToken);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return ParseRouteGet(target, output);
        }

        return new RouteDecision(target, null, null, string.Empty, "No route information available", ReachabilityKind.Unknown);
    }

    public async Task<PortReachability> TestPortAsync(IPAddress target, int port, string serviceName, CancellationToken cancellationToken = default)
    {
        var result = await new PortScanner().ScanPortAsync(target, port, TimeSpan.FromMilliseconds(900), cancellationToken);
        return new PortReachability(target, port, result.IsOpen, serviceName);
    }

    public async Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var output = await ProcessHelper.RunAsync("traceroute", $"-n -m 8 -w 1 {target}", TimeSpan.FromSeconds(20), cancellationToken);
        var hops = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .ToArray();

        return new TraceRouteResult(target, hops, hops.Length == 0 ? "No trace hops returned" : string.Join(" -> ", hops));
    }

    private static RouteDecision ParseRouteGet(IPAddress target, string output)
    {
        // route -n get output:
        //    route to: 192.168.87.243
        // destination: 192.168.87.0
        //    gateway: 192.168.0.1
        //  interface: en0
        IPAddress? gateway = null;
        var interfaceAlias = string.Empty;

        foreach (var line in output.Split(Environment.NewLine))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("gateway:", StringComparison.OrdinalIgnoreCase))
            {
                var gw = trimmed.Substring("gateway:".Length).Trim();
                IPAddress.TryParse(gw, out gateway);
            }
            else if (trimmed.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
            {
                interfaceAlias = trimmed.Substring("interface:".Length).Trim();
            }
        }

        var summary = gateway is null
            ? $"Direct route on {interfaceAlias}".Trim()
            : $"via {gateway} on {interfaceAlias}".Trim();

        return new RouteDecision(target, null, gateway, interfaceAlias, summary, ReachabilityKind.Unknown);
    }
}
