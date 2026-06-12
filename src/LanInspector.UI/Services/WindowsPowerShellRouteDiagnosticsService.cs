using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Scanning;

namespace LanInspector.UI.Services;

public sealed class WindowsPowerShellRouteDiagnosticsService : IRouteDiagnosticsService
{
    public async Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var script = $"Find-NetRoute -RemoteIPAddress {target} | Select-Object -First 1 InterfaceAlias,NextHop,IPAddress,RouteMetric | ConvertTo-Json -Compress";
        var output = await RunPowerShellAsync(script, TimeSpan.FromSeconds(5), cancellationToken);
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

            var summary = nextHop is null || nextHop.Equals(IPAddress.Any)
                ? $"Direct route on {alias}".Trim()
                : $"via {nextHop} on {alias}".Trim();

            return new RouteDecision(target, source, nextHop, alias, summary, ReachabilityKind.Unknown);
        }
        catch
        {
            return new RouteDecision(target, null, null, string.Empty, output.Trim(), ReachabilityKind.Unknown);
        }
    }

    public async Task<PortReachability> TestPortAsync(
        IPAddress target,
        int port,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        var result = await new PortScanner().ScanPortAsync(target, port, TimeSpan.FromMilliseconds(900), cancellationToken);
        return new PortReachability(target, port, result.IsOpen, serviceName);
    }

    public async Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken cancellationToken = default)
    {
        var output = await RunProcessAsync("tracert.exe", $"-d -h 8 -w 750 {target}", TimeSpan.FromSeconds(10), cancellationToken);
        var hops = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .ToArray();

        return new TraceRouteResult(target, hops, hops.Length == 0 ? "No trace hops returned" : string.Join(" -> ", hops));
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? property.ToString() : null;
    }

    private static Task<string> RunPowerShellAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"", timeout, cancellationToken);
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await outputTask;
            var error = await errorTask;
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }
}
