using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;

namespace LanInspector.Cli;

internal static class CliApp
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return;
        }

        var routeDiag = PlatformServiceFactory.CreateRouteDiagnosticsService();
        var terminalLauncher = PlatformServiceFactory.CreateTerminalLauncher();
        var capturePrereqs = PlatformServiceFactory.CreateCapturePrerequisiteService();
        var tailscale = PlatformServiceFactory.CreateTailscaleService();
        var knownDevices = LoadKnownDevices();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        switch (command)
        {
            case "status":
                await RunStatusAsync(routeDiag, tailscale, ct);
                break;

            case "interfaces":
                RunInterfaces();
                break;

            case "known":
                RunKnown(knownDevices);
                break;

            case "check":
                await RunCheckKnownAsync(rest, knownDevices, routeDiag, ct);
                break;

            case "check-ip":
                await RunCheckIpAsync(rest, routeDiag, ct);
                break;

            case "route":
                await RunRouteAsync(rest, routeDiag, ct);
                break;

            case "trace":
                await RunTraceAsync(rest, routeDiag, ct);
                break;

            case "ssh":
                await RunSshAsync(rest, knownDevices, terminalLauncher, tailscale, ct);
                break;

            case "tailscale":
                await RunTailscaleAsync(rest, tailscale, knownDevices, ct);
                break;

            case "recommend":
                await RunRecommendAsync(rest, knownDevices, routeDiag, tailscale, ct);
                break;

            case "capture-prereqs":
                await RunCapturePrereqsAsync(capturePrereqs, ct);
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                Console.Error.WriteLine("Run 'laninspector help' for usage.");
                Environment.Exit(1);
                break;
        }
    }

    private static async Task RunStatusAsync(IRouteDiagnosticsService routeDiag, ITailscaleService tailscale, CancellationToken ct)
    {
        Console.WriteLine("LanInspector Status");
        Console.WriteLine(new string('-', 40));

        Console.WriteLine($"OS:    {GetOsName()}");
        Console.WriteLine($"Host:  {Environment.MachineName}");

        var profile = new LocalNetworkProfileProvider().GetCurrentProfile();
        if (profile.Interfaces.Count == 0)
        {
            Console.WriteLine("Network: No active IPv4 interface detected.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Network interfaces:");
            foreach (var iface in profile.Interfaces)
            {
                Console.WriteLine($"  {iface.Name}: {iface.Address} on {iface.Network} via {iface.GatewayAddress}");
            }
        }

        Console.WriteLine();
        var ts = await tailscale.GetStatusAsync(ct);
        Console.WriteLine("Tailscale:");
        Console.WriteLine($"  State:  {ts.State}");
        if (ts.State == TailscaleConnectionState.Connected)
        {
            Console.WriteLine($"  Name:   {ts.LocalName}");
            Console.WriteLine($"  IPs:    {string.Join(", ", ts.LocalIps)}");
            Console.WriteLine($"  Peers:  {ts.Peers.Count} ({ts.Peers.Count(p => p.IsOnline)} online)");
        }
    }

    private static void RunInterfaces()
    {
        Console.WriteLine("Network Interfaces");
        Console.WriteLine(new string('-', 40));

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        foreach (var iface in interfaces)
        {
            var ipProps = iface.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}/{a.PrefixLength}")
                .FirstOrDefault() ?? "(no IPv4)";

            Console.WriteLine($"  {iface.Name,-20} {iface.Description,-35} {ipv4}");
        }
    }

    private static void RunKnown(IReadOnlyList<KnownDeviceDefinition> knownDevices)
    {
        Console.WriteLine("Known Devices");
        Console.WriteLine(new string('-', 40));

        if (knownDevices.Count == 0)
        {
            Console.WriteLine("No known devices configured.");
            Console.WriteLine("Create known-devices.json in the current directory or ~/.config/laninspector/");
            return;
        }

        foreach (var device in knownDevices)
        {
            var tags = device.Tags.Count > 0 ? $"[{string.Join(", ", device.Tags)}]" : string.Empty;
            Console.WriteLine($"  {device.Id,-20} {device.DisplayName,-30} {tags}");
            if (device.KnownIps.Count > 0)
            {
                Console.WriteLine($"    IPs: {string.Join(", ", device.KnownIps)}");
            }

            if (device.KnownTailscaleNames.Count > 0)
            {
                Console.WriteLine($"    Tailscale: {string.Join(", ", device.KnownTailscaleNames)}");
            }

            if (device.Ssh?.Enabled == true)
            {
                Console.WriteLine($"    SSH: {device.Ssh.User}@... port {device.Ssh.Port}");
            }
        }
    }

    private static async Task RunCheckKnownAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, IRouteDiagnosticsService routeDiag, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laninspector check <device-id>");
            return;
        }

        var id = args[0];
        var device = knownDevices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            Console.Error.WriteLine($"Device '{id}' not found in known devices.");
            return;
        }

        Console.WriteLine($"LanInspector Check: {device.DisplayName}");
        Console.WriteLine(new string('-', 40));

        foreach (var ipStr in device.KnownIps)
        {
            if (!IPAddress.TryParse(ipStr, out var ip))
            {
                continue;
            }

            Console.WriteLine($"\nTarget IP: {ipStr}");
            await PrintRouteAndPortAsync(ip, device.Ssh?.Port ?? 22, "SSH", routeDiag, ct);
        }
    }

    private static async Task RunCheckIpAsync(string[] args, IRouteDiagnosticsService routeDiag, CancellationToken ct)
    {
        if (args.Length == 0 || !IPAddress.TryParse(args[0], out var ip))
        {
            Console.Error.WriteLine("Usage: laninspector check-ip <ip> [--port <port>]");
            return;
        }

        var port = 22;
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var parsedPort))
        {
            port = parsedPort;
        }

        Console.WriteLine($"LanInspector Check IP: {ip}");
        Console.WriteLine(new string('-', 40));
        await PrintRouteAndPortAsync(ip, port, port == 22 ? "SSH" : $"port {port}", routeDiag, ct);
    }

    private static async Task RunRouteAsync(string[] args, IRouteDiagnosticsService routeDiag, CancellationToken ct)
    {
        if (args.Length == 0 || !IPAddress.TryParse(args[0], out var ip))
        {
            Console.Error.WriteLine("Usage: laninspector route <ip>");
            return;
        }

        Console.WriteLine($"LanInspector Route Check: {ip}");
        Console.WriteLine(new string('-', 40));

        var route = await routeDiag.GetRouteToAsync(ip, ct);
        Console.WriteLine($"Route:      {route.RouteSummary}");
        if (route.NextHop is not null)
        {
            Console.WriteLine($"Next hop:   {route.NextHop}");
            if (RouteHelpers.IsCgnatOrTailscale(route.NextHop) && RouteHelpers.IsRfc1918(ip))
            {
                Console.WriteLine();
                Console.WriteLine("Warning: Traffic to this private IP is being routed upstream.");
                Console.WriteLine("         The local router does not know how to reach the target subnet.");
                Console.WriteLine("         Consider: Tailscale, or connecting to the network containing the target.");
            }
        }

        if (route.SourceAddress is not null)
        {
            Console.WriteLine($"Source:     {route.SourceAddress}");
        }

        Console.WriteLine($"Interface:  {route.InterfaceAlias}");
    }

    private static async Task RunTraceAsync(string[] args, IRouteDiagnosticsService routeDiag, CancellationToken ct)
    {
        if (args.Length == 0 || !IPAddress.TryParse(args[0], out var ip))
        {
            Console.Error.WriteLine("Usage: laninspector trace <ip>");
            return;
        }

        Console.WriteLine($"LanInspector Traceroute: {ip}");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine("Tracing (this may take a few seconds)...");

        var trace = await routeDiag.TraceRouteAsync(ip, ct);
        if (trace.Hops.Count == 0)
        {
            Console.WriteLine("No hops returned. The target may be unreachable or ICMP is blocked.");
            return;
        }

        foreach (var hop in trace.Hops)
        {
            Console.WriteLine($"  {hop}");
        }

        Console.WriteLine();
        var route = await routeDiag.GetRouteToAsync(ip, ct);
        var misconfig = RouteHelpers.DetectMisconfiguration(route, trace);
        if (misconfig is not null)
        {
            Console.WriteLine($"Diagnosis: {misconfig.UserFriendly}");
        }
    }

    private static async Task RunSshAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, ITerminalLauncher launcher, ITailscaleService tailscale, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laninspector ssh <device-id> [--print] [--open]");
            return;
        }

        var id = args[0];
        var printOnly = args.Contains("--print");
        var openTerminal = args.Contains("--open");

        var device = knownDevices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            Console.Error.WriteLine($"Device '{id}' not found in known devices.");
            return;
        }

        if (device.Ssh?.Enabled != true || string.IsNullOrWhiteSpace(device.Ssh.User))
        {
            Console.Error.WriteLine($"Device '{id}' does not have SSH configured.");
            return;
        }

        // Prefer Tailscale name if available
        var tailscaleStatus = await tailscale.GetStatusAsync(ct);
        var preferredHost = FindBestHost(device, tailscaleStatus);
        var command = SshCommandGenerator.Generate(device.Ssh.User, preferredHost, device.Ssh.Port);

        Console.WriteLine($"SSH command: {command}");

        if (printOnly || !openTerminal)
        {
            return;
        }

        var launched = await launcher.LaunchSshAsync(command, ct);
        Console.WriteLine(launched ? "Terminal launched." : "Could not launch terminal. Copy the command above.");
    }

    private static async Task RunTailscaleAsync(string[] args, ITailscaleService tailscale, IReadOnlyList<KnownDeviceDefinition> knownDevices, CancellationToken ct)
    {
        var subCommand = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        var status = await tailscale.GetStatusAsync(ct);

        if (subCommand == "routes")
        {
            Console.WriteLine("Tailscale Subnet Route Assistant");
            Console.WriteLine(new string('-', 40));

            var serverDevices = knownDevices.Where(d => d.Tags.Contains("server", StringComparer.OrdinalIgnoreCase)).ToArray();
            if (serverDevices.Length == 0)
            {
                Console.WriteLine("No server devices found in known-devices configuration.");
                Console.WriteLine("Tag a device with 'server' to see subnet route suggestions.");
                return;
            }

            foreach (var device in serverDevices)
            {
                var cmd = SubnetRouteAssistant.BuildCommand(device);
                if (cmd is null)
                {
                    continue;
                }

                Console.WriteLine($"\nFor {device.DisplayName}:");
                Console.WriteLine($"  {cmd}");
                Console.WriteLine();
                Console.WriteLine("  Run this on the Linux server that can reach the target subnet.");
                Console.WriteLine("  Then approve the advertised route in the Tailscale admin console.");
            }

            Console.WriteLine();
            Console.WriteLine("Warning: If your trace shows 100.64.x.x/CGNAT, inbound port forwarding");
            Console.WriteLine("         may not work. Prefer Tailscale or Cloudflare Tunnel.");
            return;
        }

        // Default: status
        Console.WriteLine("Tailscale Status");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"State: {status.State}");

        switch (status.State)
        {
            case TailscaleConnectionState.NotInstalled:
                Console.WriteLine("Tailscale is not installed or not in PATH.");
                Console.WriteLine("Install from: https://tailscale.com/download");
                break;

            case TailscaleConnectionState.InstalledNotConnected:
                Console.WriteLine("Tailscale is installed but not connected.");
                Console.WriteLine("Run: tailscale up");
                break;

            case TailscaleConnectionState.Connected:
                Console.WriteLine($"Name:  {status.LocalName}");
                Console.WriteLine($"IPs:   {string.Join(", ", status.LocalIps)}");
                Console.WriteLine();
                Console.WriteLine($"Peers ({status.Peers.Count}):");
                foreach (var peer in status.Peers.OrderByDescending(p => p.IsOnline))
                {
                    var onlineMark = peer.IsOnline ? "(online)" : "(offline)";
                    var ips = string.Join(", ", peer.TailscaleIps);
                    Console.WriteLine($"  {peer.Name,-25} {onlineMark,-10} {ips}");
                }

                var knownInTailnet = knownDevices.Where(d =>
                    status.Peers.Any(p =>
                        d.KnownTailscaleNames.Any(n =>
                            string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n, p.DnsName, StringComparison.OrdinalIgnoreCase)))).ToArray();

                if (knownInTailnet.Length > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Known devices found in Tailnet:");
                    foreach (var d in knownInTailnet)
                    {
                        Console.WriteLine($"  {d.DisplayName}");
                    }
                }

                break;
        }
    }

    private static async Task RunRecommendAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, IRouteDiagnosticsService routeDiag, ITailscaleService tailscale, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laninspector recommend <device-id>");
            return;
        }

        var id = args[0];
        var device = knownDevices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            Console.Error.WriteLine($"Device '{id}' not found in known devices.");
            return;
        }

        Console.WriteLine($"LanInspector Connection Recommendation: {device.DisplayName}");
        Console.WriteLine(new string('-', 50));

        var profile = new LocalNetworkProfileProvider().GetCurrentProfile();
        var primaryInterface = profile.Interfaces.FirstOrDefault();
        if (primaryInterface is not null)
        {
            Console.WriteLine($"Current machine: {primaryInterface.Address} on {primaryInterface.Network} via {primaryInterface.GatewayAddress}");
        }

        Console.WriteLine();
        Console.WriteLine("Testing connectivity...");

        var engine = new RemoteAccessRecommendationEngine(
            new LocalNetworkProfileProvider(),
            routeDiag,
            tailscale,
            new PortScanner());

        var recommendation = await engine.RecommendAsync(device, ct);

        if (recommendation.CgnatWarning is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Diagnosis: {recommendation.CgnatWarning}");
        }

        Console.WriteLine();
        if (recommendation.Candidates.Count == 0)
        {
            Console.WriteLine("No reachable path found.");
        }
        else
        {
            Console.WriteLine("Recommended access methods (in order of preference):");
            for (var i = 0; i < recommendation.Candidates.Count; i++)
            {
                var c = recommendation.Candidates[i];
                Console.WriteLine($"  {i + 1}. {c.Description}");
                Console.WriteLine($"     {c.SshCommand}");
            }
        }

        if (recommendation.SubnetRouteCommand is not null)
        {
            Console.WriteLine();
            Console.WriteLine("Subnet route suggestion (run on Linux server that can reach the target):");
            Console.WriteLine($"  {recommendation.SubnetRouteCommand}");
            Console.WriteLine("  Then approve the route in the Tailscale admin console.");
        }
    }

    private static async Task RunCapturePrereqsAsync(ICapturePrerequisiteService prereqs, CancellationToken ct)
    {
        Console.WriteLine("Capture Prerequisites");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"OS: {GetOsName()}");
        Console.WriteLine();

        var status = await prereqs.CheckAsync(ct);
        Console.WriteLine($"Status: {status.Kind}");
        Console.WriteLine($"Summary: {status.Summary}");

        if (!string.IsNullOrWhiteSpace(status.Suggestion))
        {
            Console.WriteLine();
            Console.WriteLine(status.Suggestion);
        }

        Console.WriteLine();
        Console.WriteLine("Note: Route diagnostics, Tailscale, and SSH command generation work without packet capture.");
    }

    private static async Task PrintRouteAndPortAsync(IPAddress ip, int port, string serviceName, IRouteDiagnosticsService routeDiag, CancellationToken ct)
    {
        var route = await routeDiag.GetRouteToAsync(ip, ct);
        Console.WriteLine($"  Route:      {route.RouteSummary}");

        var portResult = await routeDiag.TestPortAsync(ip, port, serviceName, ct);
        Console.WriteLine($"  {serviceName} port {port}: {(portResult.IsOpen ? "Open" : "Closed/unreachable")}");

        var misconfig = RouteHelpers.DetectMisconfiguration(route);
        if (misconfig is not null)
        {
            Console.WriteLine($"  Diagnosis:  {misconfig.UserFriendly}");
        }
    }

    private static string FindBestHost(KnownDeviceDefinition device, TailscaleStatus tailscaleStatus)
    {
        if (tailscaleStatus.State == TailscaleConnectionState.Connected && device.KnownTailscaleNames.Count > 0)
        {
            var peer = tailscaleStatus.Peers.FirstOrDefault(p =>
                device.KnownTailscaleNames.Any(n =>
                    string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)));
            if (peer is not null && peer.IsOnline)
            {
                return peer.Name;
            }
        }

        return device.KnownIps.FirstOrDefault() ?? device.Id;
    }

    private static IReadOnlyList<KnownDeviceDefinition> LoadKnownDevices()
    {
        var searchPaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "known-devices.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "known-devices.local.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "laninspector", "known-devices.json"),
            Path.Combine(AppContext.BaseDirectory, "Data", "known-devices.json"),
            Path.Combine(AppContext.BaseDirectory, "known-devices.json")
        };

        var existingPaths = searchPaths.Where(File.Exists).ToArray();
        if (existingPaths.Length == 0)
        {
            return [];
        }

        return KnownDevicesConfiguration.LoadMany(existingPaths).KnownDevices;
    }

    private static string GetOsName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsLinux()) return "Linux";
        return "Unknown";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LanInspector CLI");
        Console.WriteLine();
        Console.WriteLine("Usage: laninspector <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  status                     Show current network and Tailscale status");
        Console.WriteLine("  interfaces                 List network interfaces");
        Console.WriteLine("  known                      List known devices from config");
        Console.WriteLine("  check <id>                 Check reachability of a known device");
        Console.WriteLine("  check-ip <ip> [--port <p>] Check reachability of an IP/port");
        Console.WriteLine("  route <ip>                 Show route to an IP address");
        Console.WriteLine("  trace <ip>                 Traceroute to an IP address");
        Console.WriteLine("  ssh <id> [--print|--open]  SSH command for a known device");
        Console.WriteLine("  tailscale status           Tailscale status and peer list");
        Console.WriteLine("  tailscale routes           Subnet route command suggestions");
        Console.WriteLine("  recommend <id>             Connection recommendations for known device");
        Console.WriteLine("  capture-prereqs            Check packet capture prerequisites");
        Console.WriteLine();
        Console.WriteLine("Known device config is loaded from (first match wins):");
        Console.WriteLine("  ./known-devices.json");
        Console.WriteLine("  ~/.config/laninspector/known-devices.json");
        Console.WriteLine("  <exe-dir>/Data/known-devices.json");
        Console.WriteLine();
        Console.WriteLine("No passwords are stored. SSH uses your local keys and ssh-agent.");
    }
}
