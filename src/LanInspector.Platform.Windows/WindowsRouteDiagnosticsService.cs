using System.Net;
using System.Text.Json;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Scanning;

namespace LanInspector.Platform.Windows;

public sealed class WindowsRouteDiagnosticsService : IRouteDiagnosticsService
{
    public async Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var sanitizedTarget = target.ToString();
        if (!IPAddress.TryParse(sanitizedTarget, out _))
        {
            return new RouteDecision(target, null, null, string.Empty, "Invalid IP address", ReachabilityKind.Unknown);
        }

        var script = $"Find-NetRoute -RemoteIPAddress {sanitizedTarget} | Select-Object -First 1 InterfaceAlias,NextHop,IPAddress,RouteMetric | ConvertTo-Json -Compress";
        var output = await ProcessHelper.RunAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", TimeSpan.FromSeconds(5), cancellationToken);

        if (string.IsNullOrWhiteSpace(output))
        {
            return new RouteDecision(target, null, null, string.Empty, "No route decision returned", ReachabilityKind.Unknown);
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var nextHopText = TryGetString(root, "NextHop");
            var sourceText = TryGetString(root, "IPAddress");
            var alias = TryGetString(root, "InterfaceAlias") ?? string.Empty;
            IPAddress.TryParse(nextHopText, out var nextHop);
            IPAddress.TryParse(sourceText, out var source);
            nextHop = nextHop is not null && nextHop.Equals(IPAddress.Any) ? null : nextHop;

            var summary = nextHop is null
                ? $"Direct route on {alias}".Trim()
                : $"via {nextHop} on {alias}".Trim();

            return new RouteDecision(target, source, nextHop, alias, summary, ReachabilityKind.Unknown);
        }
        catch
        {
            return new RouteDecision(target, null, null, string.Empty, output.Trim(), ReachabilityKind.Unknown);
        }
    }

    public async Task<PortReachability> TestPortAsync(IPAddress target, int port, string serviceName, CancellationToken cancellationToken = default)
    {
        var result = await new PortScanner().ScanPortAsync(target, port, TimeSpan.FromMilliseconds(900), cancellationToken);
        return new PortReachability(target, port, result.IsOpen, serviceName);
    }

    public async Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var sanitizedTarget = target.ToString();
        if (!IPAddress.TryParse(sanitizedTarget, out _))
        {
            return new TraceRouteResult(target, Array.Empty<string>(), "Invalid IP address");
        }

        var output = await ProcessHelper.RunAsync("tracert.exe", $"-d -h 8 -w 750 {sanitizedTarget}", TimeSpan.FromSeconds(10), cancellationToken);
        var hops = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .ToArray();

        return new TraceRouteResult(target, hops, hops.Length == 0 ? "No trace hops returned" : string.Join(" -> ", hops));
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.ToString() : null;
}
