using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Dns;
using LanInspector.Core.Network;
using LanInspector.Core.Nmap;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;
using LanInspector.Core.Snmp;
using LanInspector.Core.Topology;
using LanInspector.Core.Tshark;
using LanInspector.Core.Visibility;

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

            case "topology":
                await RunTopologyAsync(rest, knownDevices, tailscale, ct);
                break;

            case "visibility":
                await RunVisibilityAsync(rest, knownDevices, routeDiag, tailscale, ct);
                break;

            case "nmap":
                await RunNmapAsync(rest, ct);
                break;

            case "wireshark":
            case "tshark":
                await RunTsharkAsync(rest, command, ct);
                break;

            case "pcap":
                await RunPcapAsync(rest, ct);
                break;

            case "dns":
                await RunDnsAsync(rest, ct);
                break;

            case "snmp":
                await RunSnmpAsync(rest, ct);
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

    private static async Task RunTopologyAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, ITailscaleService tailscale, CancellationToken ct)
    {
        var outputJson = args.Contains("--json");
        var outputMermaid = args.Contains("--mermaid");

        var profile = new LocalNetworkProfileProvider().GetCurrentProfile();
        var ts = await tailscale.GetStatusAsync(ct);

        var builder = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddKnownDevices(knownDevices, [])
            .AddTailscaleStatus(ts);

        var snapshot = builder.Build();

        if (outputJson)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                capturedAt = snapshot.CapturedAt,
                nodes = snapshot.Nodes.Select(n => new { n.Id, n.DisplayName, type = n.Type.ToString(), confidence = n.Confidence.ToString(), ip = n.PrimaryIp?.ToString(), n.MacAddress, n.Evidence }),
                edges = snapshot.Edges.Select(e => new { e.FromId, e.ToId, linkType = e.LinkType.ToString(), confidence = e.Confidence.ToString(), e.Label })
            }, opts));
            return;
        }

        if (outputMermaid)
        {
            Console.WriteLine(snapshot.ToMermaid());
            return;
        }

        Console.WriteLine($"Network Topology Snapshot — {snapshot.CapturedAt:u}");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"Nodes: {snapshot.Nodes.Count}  Edges: {snapshot.Edges.Count}");
        Console.WriteLine();

        foreach (var node in snapshot.Nodes)
        {
            var ip = node.PrimaryIp is not null ? $" [{node.PrimaryIp}]" : "";
            var conf = $"({node.Confidence})";
            Console.WriteLine($"  {node.Type,-14} {node.DisplayName,-30}{ip} {conf}");
            foreach (var e in node.Evidence)
                Console.WriteLine($"    + {e}");
        }

        Console.WriteLine();
        Console.WriteLine("Connections:");
        foreach (var edge in snapshot.Edges)
        {
            var from = snapshot.FindNode(edge.FromId)?.DisplayName ?? edge.FromId;
            var to = snapshot.FindNode(edge.ToId)?.DisplayName ?? edge.ToId;
            Console.WriteLine($"  {from} --[{edge.LinkType}]--> {to}");
        }

        Console.WriteLine();
        Console.WriteLine("Tip: use --json or --mermaid for other output formats");
    }

    private static async Task RunVisibilityAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, IRouteDiagnosticsService routeDiag, ITailscaleService tailscale, CancellationToken ct)
    {
        var config = new KnownDevicesConfiguration { KnownDevices = [.. knownDevices] };
        var svc = new VisibilityExplanationService(routeDiag, new LocalNetworkProfileProvider(), config, tailscale);

        var targetArg = args.FirstOrDefault(a => !a.StartsWith("--"));

        if (targetArg is not null && IPAddress.TryParse(targetArg, out var targetIp))
        {
            var ex = await svc.ExplainAsync(targetIp, ct);
            PrintVisibility(ex);
            return;
        }

        Console.WriteLine("LanInspector Visibility — All Known Devices");
        Console.WriteLine(new string('-', 50));

        var results = await svc.ExplainAllKnownAsync(ct);
        if (results.Count == 0)
        {
            Console.WriteLine("No known devices to check. Add them to known-devices.json.");
            return;
        }

        foreach (var ex in results)
            PrintVisibility(ex);
    }

    private static void PrintVisibility(VisibilityExplanation ex)
    {
        var indicator = ex.Result switch
        {
            VisibilityResult.Visible => "[VISIBLE]",
            VisibilityResult.ProbablyVisible => "[PROBABLY VISIBLE]",
            VisibilityResult.ProbablyNotVisible => "[PROBABLY NOT VISIBLE]",
            VisibilityResult.NotVisible => "[NOT VISIBLE]",
            _ => "[UNKNOWN]"
        };

        Console.WriteLine($"\n{indicator} {ex.TargetLabel} ({ex.Target})");
        Console.WriteLine($"  {ex.Summary}");

        if (ex.CanSee.Count > 0)
        {
            Console.WriteLine("  Can see:");
            foreach (var s in ex.CanSee) Console.WriteLine($"    + {s}");
        }

        if (ex.CannotSee.Count > 0)
        {
            Console.WriteLine("  Cannot see:");
            foreach (var s in ex.CannotSee) Console.WriteLine($"    - {s}");
        }

        if (ex.Why.Count > 0)
        {
            Console.WriteLine("  Why:");
            foreach (var s in ex.Why) Console.WriteLine($"    * {s}");
        }

        if (ex.HowToImprove.Count > 0)
        {
            Console.WriteLine("  How to improve:");
            foreach (var s in ex.HowToImprove) Console.WriteLine($"    > {s}");
        }
    }

    private static async Task RunNmapAsync(string[] args, CancellationToken ct)
    {
        var nmap = new NmapService();

        if (args.Length == 0 || args[0] == "status")
        {
            Console.WriteLine($"Nmap: {(nmap.IsAvailable ? $"Found at {nmap.NmapPath}" : "Not installed")}");
            return;
        }

        var sub = args[0].ToLowerInvariant();
        var target = args.Length > 1 ? args[1] : null;

        if (target is null)
        {
            Console.Error.WriteLine($"Usage: laninspector nmap {sub} <target>");
            return;
        }

        var mode = sub switch
        {
            "ping" => NmapScanMode.Ping,
            "ports" => NmapScanMode.TcpConnect,
            "services" => NmapScanMode.ServiceDetect,
            _ => NmapScanMode.Ping
        };

        Console.WriteLine($"Running nmap {mode} scan on {target}...");

        using var longCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        longCt.CancelAfter(TimeSpan.FromMinutes(10));

        var result = await nmap.ScanAsync(target, mode, longCt.Token);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"Scan failed: {result.ErrorMessage}");
            return;
        }

        Console.WriteLine($"Scan completed in {(result.FinishedAt - result.StartedAt).TotalSeconds:F1}s — {result.Hosts.Count} host(s)");
        Console.WriteLine();

        foreach (var host in result.Hosts)
        {
            Console.WriteLine($"  {host.Address}  {host.Hostname ?? ""}  [{host.Status}]");
            foreach (var port in host.Ports.Where(p => p.State == "open"))
                Console.WriteLine($"    {port.Number}/{port.Protocol,-4} {port.Service,-20} {port.Version ?? ""}");
        }
    }

    private static async Task RunTsharkAsync(string[] args, string command, CancellationToken ct)
    {
        var svc = new TsharkService();

        if (args.Length == 0 || args[0] == "status")
        {
            Console.WriteLine($"tshark:    {(svc.IsTsharkAvailable ? $"Found at {svc.TsharkPath}" : "Not installed")}");
            Console.WriteLine($"Wireshark: {(svc.IsWiresharkAvailable ? $"Found at {svc.WiresharkPath}" : "Not installed")}");
            return;
        }

        var sub = args[0].ToLowerInvariant();
        var file = args.Length > 1 ? args[1] : null;

        if ((sub == "summary" || sub == "open") && file is null)
        {
            Console.Error.WriteLine($"Usage: laninspector {command} {sub} <pcap-file>");
            return;
        }

        if (sub == "summary")
        {
            Console.WriteLine($"Reading {file}...");
            var packets = await svc.ReadSummaryAsync(file!, ct);
            Console.WriteLine($"  {packets.Count} packets");
            foreach (var p in packets.Take(20))
                Console.WriteLine($"  {p.Number,6} {p.TimestampSeconds,10:F3}  {p.Source,-20} -> {p.Destination,-20}  {p.Protocol,-10} {p.Length,5} {p.Info}");
        }
        else if (sub == "open")
        {
            var opened = await svc.OpenInWiresharkAsync(file!, ct);
            Console.WriteLine(opened ? $"Opened {file} in Wireshark" : "Failed to open Wireshark");
        }
        else
        {
            Console.Error.WriteLine($"Unknown {command} subcommand: {sub}");
            Console.Error.WriteLine($"Usage: laninspector {command} status|summary <file>|open <file>");
        }
    }

    private static async Task RunPcapAsync(string[] args, CancellationToken ct)
    {
        var svc = new TsharkService();

        if (args.Length == 0 || args[0] != "export")
        {
            Console.Error.WriteLine("Usage: laninspector pcap export <device> <seconds> [<output.pcapng>]");
            return;
        }

        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: laninspector pcap export <device> <seconds> [<output.pcapng>]");
            return;
        }

        var device = args[1];
        if (!int.TryParse(args[2], out var seconds) || seconds < 1)
        {
            Console.Error.WriteLine("Duration must be a positive integer (seconds).");
            return;
        }

        var output = args.Length > 3 ? args[3] : Path.Combine(Directory.GetCurrentDirectory(), $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcapng");

        Console.WriteLine($"Capturing {seconds}s from {device} → {output}...");
        var result = await svc.ExportPcapngAsync(device, TimeSpan.FromSeconds(seconds), output, ct);

        if (result.Succeeded)
            Console.WriteLine($"Saved: {result.FilePath}");
        else
            Console.Error.WriteLine($"Export failed: {result.ErrorMessage}");
    }

    private static async Task RunDnsAsync(string[] args, CancellationToken ct)
    {
        var intConfig = DnsIntegrationsConfigLoader.Load();
        var svc = DnsIntegrationsConfigLoader.CreateService(intConfig);

        if (svc is null)
        {
            Console.WriteLine("No DNS filter provider configured.");
            Console.WriteLine($"Create integrations.json at: {DnsIntegrationsConfigLoader.GetDefaultPath()}");
            Console.WriteLine();
            Console.WriteLine("Example (AdGuard Home):");
            Console.WriteLine("  { \"adGuardHome\": { \"url\": \"http://192.168.0.1:3000\", \"username\": \"admin\", \"password\": \"...\" } }");
            Console.WriteLine("Example (Pi-hole):");
            Console.WriteLine("  { \"piHole\": { \"url\": \"http://192.168.0.1\", \"apiToken\": \"...\" } }");
            return;
        }

        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "status":
            {
                var status = await svc.GetStatusAsync(ct);
                Console.WriteLine($"DNS Filter: {status.ProviderName}");
                Console.WriteLine($"  Connected:         {status.IsConnected}");
                Console.WriteLine($"  Filtering enabled: {status.FilteringEnabled}");
                Console.WriteLine($"  Blocked today:     {status.BlockedToday:N0} / {status.TotalToday:N0} ({status.BlockPercent:F1}%)");
                if (status.ErrorMessage is not null)
                    Console.WriteLine($"  Error: {status.ErrorMessage}");
                break;
            }

            case "summary":
            {
                var summary = await svc.GetSummaryAsync(ct);
                Console.WriteLine($"DNS Filter Summary: {summary.Status.ProviderName}");
                Console.WriteLine($"  Blocked: {summary.Status.BlockedToday:N0} / {summary.Status.TotalToday:N0}");
                Console.WriteLine();
                Console.WriteLine("  Top clients:");
                foreach (var c in summary.TopClients.Take(5))
                    Console.WriteLine($"    {c.ClientIp,-20} {c.QueryCount:N0} queries");
                Console.WriteLine();
                Console.WriteLine("  Top domains:");
                foreach (var d in summary.TopDomains.Take(10))
                    Console.WriteLine($"    {d.Domain,-40} {d.HitCount,6:N0} {(d.IsBlocked ? "[BLOCKED]" : "")}");
                break;
            }

            case "queries":
            {
                var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 20;
                var queries = await svc.GetRecentQueriesAsync(count, ct);
                Console.WriteLine($"Recent DNS Queries ({queries.Count}):");
                foreach (var q in queries)
                {
                    var blocked = q.WasBlocked ? " [BLOCKED]" : "";
                    Console.WriteLine($"  {q.Timestamp:HH:mm:ss}  {q.ClientIp,-20} {q.Domain,-40}{blocked}");
                }
                break;
            }

            case "client":
            {
                var clientIp = args.Length > 1 ? args[1] : null;
                if (clientIp is null) { Console.Error.WriteLine("Usage: laninspector dns client <ip>"); return; }
                var queries = await svc.GetRecentQueriesAsync(500, ct);
                var clientQueries = queries.Where(q => q.ClientIp == clientIp).ToList();
                Console.WriteLine($"Queries from {clientIp}: {clientQueries.Count}");
                foreach (var q in clientQueries)
                {
                    var blocked = q.WasBlocked ? " [BLOCKED]" : "";
                    Console.WriteLine($"  {q.Timestamp:HH:mm:ss}  {q.Domain,-40}{blocked}");
                }
                break;
            }

            default:
                Console.Error.WriteLine($"Unknown dns subcommand: {sub}");
                Console.Error.WriteLine("Usage: laninspector dns status|summary|queries|client <ip>");
                break;
        }
    }

    private static async Task RunSnmpAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || !IPAddress.TryParse(args[0], out var target))
        {
            Console.Error.WriteLine("Usage: laninspector snmp <ip> [--community <community>]");
            return;
        }

        var communityIdx = Array.IndexOf(args, "--community");
        var community = communityIdx >= 0 && communityIdx + 1 < args.Length ? args[communityIdx + 1] : "public";

        Console.WriteLine($"SNMP query to {target} (community: {community})...");

        var svc = new SnmpDiscoveryService();

        using var snmpCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        snmpCt.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await svc.QueryAsync(target, community, snmpCt.Token);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"SNMP failed: {result.ErrorMessage}");
            return;
        }

        var info = result.Info!;
        Console.WriteLine($"  sysName:     {info.SysName ?? "(none)"}");
        Console.WriteLine($"  sysDescr:    {info.SysDescr?.Split('\n').First() ?? "(none)"}");
        Console.WriteLine($"  sysLocation: {info.SysLocation ?? "(none)"}");

        if (info.Interfaces.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Interfaces:");
            foreach (var iface in info.Interfaces.Take(10))
            {
                var speed = iface.SpeedBps.HasValue ? $"{iface.SpeedBps / 1_000_000}Mbps" : "";
                Console.WriteLine($"    [{iface.Index}] {iface.Description,-30} {iface.OperStatus,-10} {speed}");
            }
        }

        if (info.IpAddresses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  IP addresses:");
            foreach (var ip in info.IpAddresses)
                Console.WriteLine($"    {ip}");
        }
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
        Console.WriteLine("  topology [--json|--mermaid] Show network topology snapshot");
        Console.WriteLine("  visibility [<ip>]           Explain visibility to target or all known devices");
        Console.WriteLine("  nmap status|ping|ports|services <target>   Run nmap scan");
        Console.WriteLine("  tshark status|summary <file>|open <file>   tshark/Wireshark tools");
        Console.WriteLine("  pcap export <device> <seconds> [<file>]    Capture PCAP via tshark");
        Console.WriteLine("  dns status|summary|queries|client <ip>     DNS filter provider");
        Console.WriteLine("  snmp <ip> [--community <c>]                SNMP query");
        Console.WriteLine();
        Console.WriteLine("Known device config is loaded from (first match wins):");
        Console.WriteLine("  ./known-devices.json");
        Console.WriteLine("  ~/.config/laninspector/known-devices.json");
        Console.WriteLine("  <exe-dir>/Data/known-devices.json");
        Console.WriteLine();
        Console.WriteLine("No passwords are stored. SSH uses your local keys and ssh-agent.");
    }
}
