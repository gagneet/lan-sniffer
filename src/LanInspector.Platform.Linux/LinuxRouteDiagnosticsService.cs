using System.Net;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Scanning;

namespace LanInspector.Platform.Linux;

public sealed class LinuxRouteDiagnosticsService : IRouteDiagnosticsService
{
    public async Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var output = await ProcessHelper.RunAsync("ip", $"route get {target}", TimeSpan.FromSeconds(5), cancellationToken);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return ParseIpRouteGet(target, output);
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
        // Try tracepath first, fall back to traceroute
        var output = await ProcessHelper.RunAsync("tracepath", $"-n {target}", TimeSpan.FromSeconds(20), cancellationToken);
        if (string.IsNullOrWhiteSpace(output) || output.Contains("not found") || output.Contains("No such file"))
        {
            output = await ProcessHelper.RunAsync("traceroute", $"-n -m 8 -w 1 {target}", TimeSpan.FromSeconds(20), cancellationToken);
        }

        var hops = ParseTraceHops(output);
        return new TraceRouteResult(target, hops, hops.Count == 0 ? "No trace hops returned" : string.Join(" -> ", hops));
    }

    private static RouteDecision ParseIpRouteGet(IPAddress target, string output)
    {
        // Output: "192.168.87.243 via 192.168.4.1 dev wlan0 src 192.168.4.32 uid 1000"
        IPAddress? nextHop = null;
        IPAddress? source = null;
        var interfaceAlias = string.Empty;

        var parts = output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "via" && IPAddress.TryParse(parts[i + 1], out var via))
            {
                nextHop = via;
            }
            else if (parts[i] == "dev")
            {
                interfaceAlias = parts[i + 1];
            }
            else if (parts[i] == "src" && IPAddress.TryParse(parts[i + 1], out var src))
            {
                source = src;
            }
        }

        var summary = nextHop is null
            ? $"Direct route on {interfaceAlias}".Trim()
            : $"via {nextHop} on {interfaceAlias}".Trim();

        return new RouteDecision(target, source, nextHop, interfaceAlias, summary, ReachabilityKind.Unknown);
    }

    private static IReadOnlyList<string> ParseTraceHops(string output)
    {
        return output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.Length > 0 && (char.IsDigit(trimmed[0]) || trimmed[0] == ' ');
            })
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("tracepath", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
