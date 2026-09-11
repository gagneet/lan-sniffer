using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Net.Sockets;
using System.Text.Json;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Dns;
using LanInspector.Core.Identity;
using LanInspector.Core.Locator;
using LanInspector.Core.Flipper;
using LanInspector.Core.Flipper.Nfc;
using LanInspector.Core.Flipper.SubGhz;
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

        if (args[0] is "-v" or "--version" or "version")
        {
            PrintVersion();
            return;
        }

        var routeDiag = PlatformServiceFactory.CreateRouteDiagnosticsService();
        var terminalLauncher = PlatformServiceFactory.CreateTerminalLauncher();
        var capturePrereqs = PlatformServiceFactory.CreateCapturePrerequisiteService();
        var tailscale = PlatformServiceFactory.CreateTailscaleService();
        var knownDevices = LoadKnownDevices();

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        // Each command gets its own budget. A single global timeout would abort scans and radio
        // captures that legitimately run for minutes, and linking a longer token to a shorter
        // parent does not extend it — the parent still cancels the child.
        using var cts = new CancellationTokenSource(GetCommandTimeout(command, rest));
        var ct = cts.Token;

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

            case "locate":
            case "whereis":
                await RunLocateAsync(rest, knownDevices, tailscale, ct);
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
                await RunSnmpAsync(rest, knownDevices, ct);
                break;

            case "flipper":
                await RunFlipperAsync(rest, knownDevices, tailscale, ct);
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

    private static DeviceLocatorService CreateLocator(ITailscaleService tailscale)
    {
        return new DeviceLocatorService(
            tailscale,
            new LocalNetworkProfileProvider(),
            new ArpTableReader(),
            new PortScanner(),
            new HostnameResolver(),
            new DeviceLocationHistoryStore());
    }

    private static async Task RunLocateAsync(
        string[] args,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        ITailscaleService tailscale,
        CancellationToken ct)
    {
        var asJson = args.Contains("--json");
        var probe = args.Contains("--probe");
        var id = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        var targets = id is null
            ? knownDevices
            : knownDevices.Where(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (targets.Count == 0)
        {
            Console.Error.WriteLine(id is null
                ? "No known devices configured. Create known-devices.json first."
                : $"Device '{id}' not found in known devices.");
            Environment.ExitCode = 1;
            return;
        }

        var options = DeviceLocatorOptions.Default with { UseTailscalePingProbe = probe };
        var locations = await CreateLocator(tailscale).LocateAllAsync(targets, options, ct);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(locations.Select(location => new
            {
                deviceId = location.DeviceId,
                displayName = location.DisplayName,
                currentAddress = location.CurrentAddress?.ToString(),
                source = location.Source?.ToString(),
                confidence = location.Confidence.ToString(),
                tailscaleAddress = location.TailscaleAddress?.ToString(),
                tailscaleName = location.TailscaleName,
                verifiedAddresses = location.VerifiedAddresses.Select(address => address.ToString()),
                isMultiHomed = location.IsMultiHomed,
                previousAddress = location.PreviousAddress?.ToString(),
                addressChangedAt = location.AddressChangedAt,
                hasMoved = location.HasMoved,
                candidates = location.Candidates.Select(candidate => new
                {
                    address = candidate.Address.ToString(),
                    source = candidate.Source.ToString(),
                    detail = candidate.Detail,
                    isVerified = candidate.IsVerified,
                    verifiedPort = candidate.VerifiedPort,
                    verifiedByIcmp = candidate.VerifiedByIcmp,
                    plausibility = candidate.Plausibility.ToString()
                }),
                evidence = location.Evidence
            }), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine("LanInspector Device Locator");
        Console.WriteLine(new string('-', 60));
        if (!probe)
        {
            Console.WriteLine("Tip: add --probe to run 'tailscale ping' when passive evidence is thin.");
        }

        foreach (var location in locations)
        {
            Console.WriteLine();
            Console.WriteLine($"{location.DisplayName} ({location.DeviceId})");
            Console.WriteLine($"  Current LAN IP : {location.CurrentAddress?.ToString() ?? "(not found)"}");
            Console.WriteLine($"  Found via      : {(location.Source is null ? "-" : DeviceLocation.Describe(location.Source.Value))}");
            Console.WriteLine($"  Confidence     : {location.Confidence}");

            if (location.IsMultiHomed)
            {
                Console.WriteLine($"  Also at        : {string.Join(", ", location.AdditionalAddresses)}  (more than one active interface)");
            }

            if (location.TailscaleAddress is not null)
            {
                Console.WriteLine($"  Tailscale      : {location.TailscaleAddress}" +
                                  (string.IsNullOrWhiteSpace(location.TailscaleName) ? "" : $"  ({location.TailscaleName})"));
            }

            if (location.HasMoved)
            {
                var changed = location.AddressChangedAt is null ? "" : $" at {location.AddressChangedAt:u}";
                Console.WriteLine($"  Changed        : was {location.PreviousAddress}{changed}");
            }

            if (location.Candidates.Count > 0)
            {
                Console.WriteLine("  Candidates:");
                foreach (var candidate in location.Candidates)
                {
                    var mark = candidate switch
                    {
                        { IsVerified: true, VerifiedByIcmp: true } => "[ping only]",
                        { IsVerified: true } => $"[open :{candidate.VerifiedPort}]",
                        { IsVerified: false } => "[no answer]",
                        _ => "[not probed]"
                    };
                    var rank = candidate.Plausibility == CandidatePlausibility.Unrelated ? " (unrelated subnet)" : string.Empty;
                    Console.WriteLine($"    {candidate.Address,-16} {mark,-14} {candidate.Detail}{rank}");
                }
            }

            if (location.Evidence.Count > 0)
            {
                Console.WriteLine("  Evidence:");
                foreach (var line in location.Evidence)
                {
                    Console.WriteLine($"    * {line}");
                }
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

        var tailscaleStatus = await tailscale.GetStatusAsync(ct);
        var location = await CreateLocator(tailscale).LocateAsync(device, DeviceLocatorOptions.Default, ct);
        var preferredHost = FindBestHost(device, tailscaleStatus, location);
        var command = SshCommandGenerator.Generate(device.Ssh.User, preferredHost, device.Ssh.Port);

        Console.WriteLine($"SSH command: {command}");
        if (location.CurrentAddress is not null && !string.Equals(preferredHost, location.CurrentAddress.ToString(), StringComparison.Ordinal))
        {
            Console.WriteLine($"  (device is currently at {location.CurrentAddress} on the LAN)");
        }

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

    /// <summary>
    /// Chooses the host to put in an SSH command: an online Tailscale peer's MagicDNS name first
    /// (it keeps working from any network and survives DHCP changes), then the address the locator
    /// just confirmed, and only then the configured address, which may be stale.
    /// </summary>
    private static string FindBestHost(KnownDeviceDefinition device, TailscaleStatus tailscaleStatus, DeviceLocation? location = null)
    {
        if (tailscaleStatus.State == TailscaleConnectionState.Connected)
        {
            var peer = tailscaleStatus.FindPeerByName(device.KnownTailscaleNames.Concat(device.KnownHostnames).Append(device.Id));
            if (peer is not null && peer.IsOnline)
            {
                // The fully-qualified MagicDNS name resolves even where the short name does not.
                return !string.IsNullOrWhiteSpace(peer.DnsName) ? peer.DnsName : peer.Name;
            }
        }

        if (location?.CurrentAddress is not null && location.Confidence != LocationConfidence.Low)
        {
            return location.CurrentAddress.ToString();
        }

        return device.KnownIps.FirstOrDefault()
            ?? device.KnownHostnames.FirstOrDefault()
            ?? device.Id;
    }

    internal static IReadOnlyList<KnownDeviceDefinition> LoadKnownDevices()
    {
        return KnownDevicesConfiguration.LoadMany(GetKnownDeviceSearchPaths().Where(File.Exists).ToArray()).KnownDevices;
    }

    /// <summary>
    /// Configuration search order, least specific first. <see cref="KnownDevicesConfiguration.LoadMany"/>
    /// merges by id with later files winning, so the shipped defaults are listed first and the
    /// user's own files last — otherwise the copy bundled next to the executable would silently
    /// override the one the user edited.
    /// </summary>
    internal static IEnumerable<string> GetKnownDeviceSearchPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Data", "known-devices.json");
        yield return Path.Combine(AppContext.BaseDirectory, "known-devices.json");
        yield return Path.Combine(AppContext.BaseDirectory, "Data", "known-devices.local.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "laninspector", "known-devices.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "laninspector", "known-devices.local.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "known-devices.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "known-devices.local.json");
    }

    /// <summary>
    /// Reads the value following a flag, returning null when the flag is absent or is the final
    /// argument (rather than indexing past the end of the array).
    /// </summary>
    internal static string? TryGetFlagValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static int TryGetFlagValue(string[] args, string flag, int fallback)
    {
        var raw = TryGetFlagValue(args, flag);
        return raw is not null && int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static TimeSpan GetCommandTimeout(string command, string[] args)
    {
        // "snmp --throughput N" waits N seconds between two counter reads, and each read walks
        // several tables at three seconds a timeout. The flat 30s budget could abort it mid-sample
        // on a slow or partly-unresponsive device, reporting a failure that was the budget's fault.
        if (command == "snmp" && args.Contains("--throughput"))
        {
            return TimeSpan.FromSeconds(TryGetFlagValue(args, "--throughput", 10)) + TimeSpan.FromMinutes(2);
        }

        return GetCommandTimeout(command);
    }

    private static TimeSpan GetCommandTimeout(string command) => command switch
    {
        // Active scans and radio captures are user-initiated and inherently slow.
        "nmap" => TimeSpan.FromMinutes(10),
        "pcap" => TimeSpan.FromMinutes(30),
        "flipper" => TimeSpan.FromMinutes(10),
        "snmp" => TimeSpan.FromMinutes(2),
        "trace" => TimeSpan.FromMinutes(2),
        "recommend" or "visibility" or "check" => TimeSpan.FromMinutes(2),
        "locate" => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromSeconds(30)
    };

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

        if (knownDevices.Count == 0)
        {
            Console.WriteLine("No known devices to check. Add them to known-devices.json.");
            return;
        }

        // Explain where each device actually is now. Explaining only the configured addresses
        // produced confident "not visible" verdicts about addresses the device had already left.
        var locations = await CreateLocator(tailscale).LocateAllAsync(knownDevices, DeviceLocatorOptions.Default, ct);

        foreach (var location in locations)
        {
            if (location.CurrentAddress is null)
            {
                Console.WriteLine();
                Console.WriteLine($"[NOT LOCATED] {location.DisplayName}");
                Console.WriteLine($"  {location.Summary}");
                continue;
            }

            if (location.Source != LocationSource.ConfiguredAddress)
            {
                Console.WriteLine();
                Console.WriteLine($"(located {location.DisplayName} at {location.CurrentAddress} via {DeviceLocation.Describe(location.Source!.Value)})");
            }

            PrintVisibility(await svc.ExplainAsync(location.CurrentAddress, ct));
        }
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

        var result = await nmap.ScanAsync(target, mode, ct);

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

    private static async Task RunSnmpAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, CancellationToken ct)
    {
        if (args.Length > 0 && string.Equals(args[0], "discover", StringComparison.OrdinalIgnoreCase))
        {
            await RunSnmpDiscoverAsync(args, knownDevices, ct);
            return;
        }

        if (args.Length == 0 || !IPAddress.TryParse(args[0], out var target))
        {
            Console.Error.WriteLine("Usage: laninspector snmp <ip> [--community <community>]");
            Console.Error.WriteLine("       laninspector snmp discover              Find which router answers SNMP");
            Console.Error.WriteLine("       laninspector snmp <ip> --throughput [n] Whole-network throughput");
            return;
        }

        var community = TryGetFlagValue(args, "--community") ?? "public";
        var svcForThroughput = new SnmpDiscoveryService();

        if (args.Contains("--throughput"))
        {
            await RunSnmpThroughputAsync(svcForThroughput, target, community, TryGetFlagValue(args, "--throughput", 10), ct);
            return;
        }

        Console.WriteLine($"SNMP query to {target} (community: {community})...");

        var svc = new SnmpDiscoveryService();

        var result = await svc.QueryAsync(target, community, ct);

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

    /// <summary>
    /// Probes every router this machine can name — each interface's gateway, every hop on the way
    /// out, and any configured router — to find one that answers SNMP.
    /// </summary>
    /// <remarks>
    /// Whole-network throughput is only available from a device that carries everyone's traffic,
    /// and on a multi-router home network it is rarely obvious which device that is or what address
    /// it answers on. Guessing one address at a time is slow and inconclusive; this tries them all
    /// and says which, if any, is usable.
    /// </remarks>
    private static async Task RunSnmpDiscoverAsync(
        string[] args,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        CancellationToken ct)
    {
        var communities = args.Contains("--community")
            ? [TryGetFlagValue(args, "--community")!]
            : new[] { "public", "private" };

        var candidates = await CollectSnmpCandidatesAsync(knownDevices, ct);

        Console.WriteLine("SNMP Discovery");
        Console.WriteLine(new string('-', 70));

        if (candidates.Count == 0)
        {
            Console.WriteLine("No gateways or configured routers found to probe.");
            return;
        }

        Console.WriteLine($"Probing {candidates.Count} address(es) with community string(s): {string.Join(", ", communities)}");
        Console.WriteLine();

        var service = new SnmpDiscoveryService();
        var answered = new List<(IPAddress Address, string Community, SnmpCountersResult Counters)>();

        foreach (var (address, why) in candidates)
        {
            foreach (var community in communities)
            {
                ct.ThrowIfCancellationRequested();

                var counters = await service.GetInterfaceCountersAsync(address, community, ct);
                if (counters.Succeeded)
                {
                    var capacity = counters.Interfaces[0].IsHighCapacity ? "64-bit counters" : "32-bit counters";
                    Console.WriteLine($"  [YES] {address,-16} {why}");
                    Console.WriteLine($"         community '{community}', {counters.Interfaces.Count} interface(s), {capacity}");
                    answered.Add((address, community, counters));
                    break;
                }

                if (community == communities[^1])
                {
                    Console.WriteLine($"  [no]  {address,-16} {why}");
                }
            }
        }

        Console.WriteLine();

        if (answered.Count == 0)
        {
            Console.WriteLine("No device answered SNMP.");
            Console.WriteLine();
            Console.WriteLine("Whole-network throughput needs a device that carries everyone's traffic and will");
            Console.WriteLine("report its counters. Most consumer mesh systems (Eero, Google Nest, Deco) expose");
            Console.WriteLine("no SNMP at all and cannot be made to. The remaining options are:");
            Console.WriteLine("  - enable SNMP in the router's admin pages, if it offers it at all");
            Console.WriteLine("  - a managed switch with port mirroring, which also gives per-device detail");
            Console.WriteLine("  - router firmware you control (OpenWrt and similar)");
            Console.WriteLine("  - for per-device visibility without bytes: point the LAN at a Pi-hole or");
            Console.WriteLine("    AdGuard Home instance and use the DNS Filter tab, which shows per-client activity");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("Usable for whole-network throughput:");
        foreach (var (address, community, _) in answered)
        {
            var communityArg = community == "public" ? "" : $" --community {community}";
            Console.WriteLine($"  laninspector snmp {address} --throughput 10{communityArg}");
        }
    }

    /// <summary>
    /// Addresses worth probing, in the order they are most likely to be the device carrying
    /// everyone's traffic: each interface's own gateway first, then the hops beyond it, then
    /// anything the configuration calls a router.
    /// </summary>
    private static async Task<IReadOnlyList<(IPAddress Address, string Why)>> CollectSnmpCandidatesAsync(
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(IPAddress, string)>();

        void Add(IPAddress address, string why)
        {
            if (RouteHelpers.IsRfc1918(address) && seen.Add(address.ToString()))
            {
                candidates.Add((address, why));
            }
        }

        var profile = new LocalNetworkProfileProvider().GetCurrentProfile();
        foreach (var iface in profile.Interfaces.Where(item => item.GatewayAddress is not null))
        {
            Add(iface.GatewayAddress!, $"default gateway on {iface.Name}");
        }

        // Hops past the first gateway are the upstream routers on a double-NAT network, and one of
        // them is the device that actually sees all outbound traffic.
        try
        {
            var trace = await PlatformServiceFactory.CreateRouteDiagnosticsService()
                .TraceRouteAsync(IPAddress.Parse("1.1.1.1"), ct);

            foreach (var hop in trace.Hops)
            {
                foreach (var part in hop.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IPAddress.TryParse(part.Trim('(', ')', '[', ']'), out var hopAddress))
                    {
                        Add(hopAddress, "upstream hop toward the internet");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A trace needs privileges on some platforms; the gateways alone are still worth trying.
        }

        foreach (var device in knownDevices.Where(device =>
                     device.DeviceType.Contains("router", StringComparison.OrdinalIgnoreCase) ||
                     device.Tags.Any(tag => tag is "router" or "gateway")))
        {
            foreach (var ip in device.KnownIps)
            {
                if (IPAddress.TryParse(ip, out var address))
                {
                    Add(address, $"configured: {device.DisplayName}");
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Samples the router's interface counters twice and reports the throughput between the
    /// readings. This is the only way to see traffic belonging to other devices: a capture on this
    /// machine cannot, because a switch does not forward other devices' unicast frames to it.
    /// </summary>
    private static async Task RunSnmpThroughputAsync(
        SnmpDiscoveryService service,
        IPAddress target,
        string community,
        int seconds,
        CancellationToken ct)
    {
        Console.WriteLine($"SNMP throughput at {target} — sampling {seconds}s apart (community: {community})");
        Console.WriteLine(new string('-', 70));

        var first = await service.GetInterfaceCountersAsync(target, community, ct);
        if (!first.Succeeded)
        {
            Console.Error.WriteLine($"Could not read interface counters: {first.ErrorMessage}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Check that SNMP is enabled on the device and that the community string matches.");
            Console.Error.WriteLine("Many consumer routers expose SNMP only on the LAN side, or not at all.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Found {first.Interfaces.Count} interface(s); counters are " +
                          (first.Interfaces[0].IsHighCapacity
                              ? "64-bit (ifHC) — reliable at any speed."
                              : "32-bit only — these wrap every few minutes on a fast link, so poll often."));
        Console.WriteLine();

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

        var second = await service.GetInterfaceCountersAsync(target, community, ct);
        if (!second.Succeeded)
        {
            Console.Error.WriteLine($"Second reading failed: {second.ErrorMessage}");
            Environment.ExitCode = 1;
            return;
        }

        var throughput = SnmpCounterMath.Diff(first.Interfaces, second.Interfaces);
        if (throughput.Count == 0)
        {
            Console.WriteLine("No interface could be differenced between the two readings.");
            return;
        }

        Console.WriteLine($"{"Interface",-28} {"Down",12} {"Up",12}   Utilisation");
        foreach (var item in throughput.OrderByDescending(item => item.TotalBytesPerSecond))
        {
            var utilisation = item.UtilisationPercent is { } percent ? $"{percent:F1}%" : "-";
            Console.WriteLine($"{Truncate(item.Description, 28),-28} {FormatRate(item.InBytesPerSecond),12} {FormatRate(item.OutBytesPerSecond),12}   {utilisation}");
        }

        Console.WriteLine();
        Console.WriteLine("The WAN interface is usually the busiest one, and its counters cover every device");
        Console.WriteLine("in the house — not just this machine.");
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "\u2026";

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000) return $"{bytesPerSecond / 1_000_000:F2} MB/s";
        if (bytesPerSecond >= 1_000) return $"{bytesPerSecond / 1_000:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    private static async Task RunFlipperAsync(string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, ITailscaleService tailscale, CancellationToken ct)
    {
        var sub  = args.Length > 0 ? args[0].ToLowerInvariant() : "detect";
        var rest = args.Skip(1).ToArray();

        await using var flipper = new FlipperSerialService();

        switch (sub)
        {
            case "detect":
                RunFlipperDetect(flipper);
                return;

            case "ports":
                RunFlipperPorts(flipper);
                return;
        }

        // Commands below require a live connection
        var port = TryGetFlagValue(rest, "--port");

        Console.Write("Connecting to Flipper Zero... ");
        var connected = await flipper.ConnectAsync(port, ct);
        if (!connected)
        {
            Console.Error.WriteLine("failed.");
            Console.Error.WriteLine("Check the USB cable and that no other application (Flipper Mobile App, qFlipper) is using the port.");
            Console.Error.WriteLine("Use 'flipper ports' to list available serial ports.");
            return;
        }

        var fw = flipper.DeviceInfo?.FirmwareVersion;
        Console.WriteLine($"connected ({flipper.DeviceInfo?.PortName})" +
                          (string.IsNullOrWhiteSpace(fw) ? "" : $"  fw {fw}"));
        Console.WriteLine();

        switch (sub)
        {
            case "info":
                PrintFlipperInfo(flipper.DeviceInfo);
                break;

            case "subghz":
                await RunFlipperSubGhzAsync(flipper, rest, ct);
                break;

            case "nfc":
                await RunFlipperNfcAsync(flipper, rest, ct);
                break;

            case "rfid":
                await RunFlipperRfidAsync(flipper, rest, ct);
                break;

            case "topology":
                await RunFlipperTopologyAsync(flipper, rest, knownDevices, tailscale, ct);
                break;

            case "cmd":
                await RunFlipperRawCmdAsync(flipper, rest, ct);
                break;

            default:
                Console.Error.WriteLine($"Unknown flipper subcommand: {sub}");
                Console.Error.WriteLine("Usage: laninspector flipper detect|ports|info|subghz|nfc|rfid|topology|cmd [options]");
                break;
        }
    }

    private static void RunFlipperDetect(FlipperSerialService flipper)
    {
        Console.WriteLine("Flipper Zero — port detection");
        Console.WriteLine(new string('-', 40));

        var ports = flipper.DetectPorts();
        if (ports.Count == 0)
        {
            Console.WriteLine("No serial ports found.");
            Console.WriteLine("Connect the Flipper Zero via USB and ensure the driver is installed.");
            return;
        }

        foreach (var p in ports)
        {
            var marker = p.LooksLikeFlipper ? " <-- likely Flipper" : "";
            Console.WriteLine($"  {p.Name,-20} {p.Description}{marker}");
        }
    }

    private static void RunFlipperPorts(FlipperSerialService flipper)
    {
        var ports = flipper.DetectPorts();
        foreach (var p in ports)
            Console.WriteLine(p.Name);
    }

    private static void PrintFlipperInfo(FlipperDeviceInfo? info)
    {
        if (info is null) { Console.WriteLine("No device info."); return; }
        Console.WriteLine("Flipper Zero Device Info");
        Console.WriteLine(new string('-', 40));
        Console.WriteLine($"  Port:     {info.PortName}");
        Console.WriteLine($"  Firmware: {Or(info.FirmwareVersion, "(unknown)")}");
        Console.WriteLine($"  Hardware: {Or(info.HardwareVersion, "(unknown)")}");
        Console.WriteLine($"  Target:   {Or(info.Target,          "(unknown)")}");
        Console.WriteLine($"  Build:    {Or(info.BuildDate,       "(unknown)")}");

        if (string.IsNullOrWhiteSpace(info.FirmwareVersion))
        {
            Console.WriteLine();
            Console.WriteLine("  Note: firmware version not exposed by this firmware build.");
            Console.WriteLine("  Run 'flipper cmd help' to see available CLI commands.");
        }
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static async Task RunFlipperSubGhzAsync(FlipperSerialService flipper, string[] args, CancellationToken ct)
    {
        // Parse --freq / --duration flags
        var freqs = new List<double>();
        var durationSec = 5;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--freq" && i + 1 < args.Length && double.TryParse(args[i + 1], out var f))
                freqs.Add(f);
            if (args[i] == "--duration" && i + 1 < args.Length && int.TryParse(args[i + 1], out var d))
                durationSec = d;
        }

        IEnumerable<double>? freqList = freqs.Count > 0 ? freqs : null;
        var dwell = TimeSpan.FromSeconds(durationSec);
        var defaults = freqList is null ? " (315, 433.92, 868.35, 915 MHz)" : "";

        Console.WriteLine($"Sub-GHz IoT scan — {dwell.TotalSeconds:0}s per frequency{defaults}");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine("Scanning... (press Ctrl+C to abort early)");
        Console.WriteLine();

        var svc    = new FlipperSubGhzService(flipper);
        var result = await svc.ScanAsync(freqList, dwell, ct);

        Console.WriteLine($"Scan complete — {result.FrequenciesScanned.Count} frequency/ies, {(int)result.Duration.TotalSeconds}s elapsed");
        Console.WriteLine();

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"Error: {result.Error}");
            return;
        }

        if (result.Signals.Count == 0)
        {
            Console.WriteLine("No signals detected. Try:");
            Console.WriteLine("  • Bring the Flipper closer to IoT devices");
            Console.WriteLine("  • Use --duration 15 for longer dwell time");
            Console.WriteLine("  • Use --freq 433.92 to focus on EU IoT band");
            return;
        }

        Console.WriteLine($"Detected {result.Signals.Count} signal(s):");
        Console.WriteLine();
        foreach (var sig in result.Signals)
        {
            Console.WriteLine($"  [{sig.FrequencyLabel}]");
            Console.WriteLine($"    Protocol : {sig.ProtocolLabel}");
            Console.WriteLine($"    RSSI     : {sig.RssiDbm:F1} dBm");
            if (sig.DecodedData is not null)
                Console.WriteLine($"    Data     : {sig.DecodedData}");
            Console.WriteLine();
        }
    }

    private static async Task RunFlipperNfcAsync(FlipperSerialService flipper, string[] args, CancellationToken ct)
    {
        var timeoutSec = 10;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--timeout" && int.TryParse(args[i + 1], out var t))
                timeoutSec = t;

        Console.WriteLine($"NFC detection — waiting up to {timeoutSec}s for a card...");
        Console.WriteLine("Hold an NFC card near the Flipper's top.");
        Console.WriteLine();

        var svc    = new FlipperNfcService(flipper);
        var result = await svc.DetectAsync(TimeSpan.FromSeconds(timeoutSec), ct);

        if (!result.Detected)
        {
            Console.WriteLine(result.Error is not null ? $"Error: {result.Error}" : "No NFC card detected within the timeout.");
            return;
        }

        Console.WriteLine("NFC card detected:");
        Console.WriteLine($"  UID:  {result.Uid ?? "(unknown)"}");
        Console.WriteLine($"  Type: {result.CardType ?? "(unknown)"}");
        if (result.Atqa is not null) Console.WriteLine($"  ATQA: {result.Atqa}");
        if (result.Sak  is not null) Console.WriteLine($"  SAK:  {result.Sak}");
    }

    private static async Task RunFlipperRfidAsync(FlipperSerialService flipper, string[] args, CancellationToken ct)
    {
        var timeoutSec = 10;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--timeout" && int.TryParse(args[i + 1], out var t))
                timeoutSec = t;

        Console.WriteLine($"125 kHz RFID detection — waiting up to {timeoutSec}s for a tag...");
        Console.WriteLine("Hold an RFID card/fob near the Flipper's top.");
        Console.WriteLine();

        var svc    = new FlipperNfcService(flipper);
        var result = await svc.ReadRfidAsync(TimeSpan.FromSeconds(timeoutSec), ct);

        if (!result.Detected)
        {
            Console.WriteLine(result.Error is not null ? $"Error: {result.Error}" : "No RFID tag detected within the timeout.");
            return;
        }

        Console.WriteLine("RFID tag detected:");
        Console.WriteLine($"  Data:     {result.Data ?? "(unknown)"}");
        Console.WriteLine($"  Protocol: {result.Protocol ?? "(unknown)"}");
    }

    private static async Task RunFlipperRawCmdAsync(FlipperSerialService flipper, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laninspector flipper cmd <command> [--timeout <sec>]");
            Console.Error.WriteLine("  Sends a raw CLI command to the Flipper and prints the response.");
            Console.Error.WriteLine("  Example: laninspector flipper cmd version");
            return;
        }

        var timeoutSec = 10;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--timeout" && int.TryParse(args[i + 1], out var t))
                timeoutSec = t;

        var command = args[0];
        var response = await flipper.ExecuteCommandAsync(command, TimeSpan.FromSeconds(timeoutSec), ct);

        Console.WriteLine($"--- response to '{command}' ---");
        Console.WriteLine(response);
        Console.WriteLine("--- end ---");
    }

    private static async Task RunFlipperTopologyAsync(FlipperSerialService flipper, string[] args, IReadOnlyList<KnownDeviceDefinition> knownDevices, ITailscaleService tailscale, CancellationToken ct)
    {
        var outputMermaid = args.Contains("--mermaid");
        var outputJson    = args.Contains("--json");

        var durationSec = 5;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--duration" && int.TryParse(args[i + 1], out var d))
                durationSec = d;

        Console.WriteLine($"Flipper topology scan — {durationSec}s per sub-GHz frequency");
        Console.WriteLine(new string('-', 50));

        var svc    = new FlipperSubGhzService(flipper);
        var scan   = await svc.ScanAsync(null, TimeSpan.FromSeconds(durationSec), ct);

        var profile = new LocalNetworkProfileProvider().GetCurrentProfile();
        var ts      = await tailscale.GetStatusAsync(ct);

        var snapshot = new TopologyBuilder()
            .AddLocalProfile(profile)
            .AddKnownDevices(knownDevices, [])
            .AddTailscaleStatus(ts)
            .AddFlipperSubGhzDevices(scan)
            .Build();

        if (outputJson)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                capturedAt = snapshot.CapturedAt,
                nodes = snapshot.Nodes.Select(n => new { n.Id, n.DisplayName, type = n.Type.ToString(), confidence = n.Confidence.ToString(), n.Evidence }),
                edges = snapshot.Edges.Select(e => new { e.FromId, e.ToId, linkType = e.LinkType.ToString(), e.Label })
            }, opts));
            return;
        }

        if (outputMermaid)
        {
            Console.WriteLine(snapshot.ToMermaid());
            return;
        }

        Console.WriteLine($"Topology — {snapshot.CapturedAt:u}");
        Console.WriteLine();

        var iotNodes = snapshot.Nodes.Where(n => n.Type == NetworkNodeType.WirelessIoT).ToList();
        if (iotNodes.Count == 0)
        {
            Console.WriteLine("No wireless IoT devices detected via sub-GHz.");
        }
        else
        {
            Console.WriteLine($"Wireless IoT devices ({iotNodes.Count}):");
            foreach (var n in iotNodes)
            {
                Console.WriteLine($"  {n.DisplayName} ({n.Confidence})");
                foreach (var e in n.Evidence) Console.WriteLine($"    + {e}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Full topology: {snapshot.Nodes.Count} nodes, {snapshot.Edges.Count} edges");
        Console.WriteLine("Tip: use --mermaid or --json for other output formats");
    }

    /// <summary>
    /// Reports the build this executable came from. A published binary is easy to keep using after
    /// the source has moved on, and an old one simply ignores flags it does not know — so being
    /// able to check the build is the difference between "the feature is broken" and "the feature
    /// is not in this copy".
    /// </summary>
    private static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        // "1.2.3+<sha>" when published with -p:SourceRevisionId.
        var parts = informational.Split('+', 2);

        Console.WriteLine($"LanInspector {parts[0]}");
        if (parts.Length > 1)
        {
            Console.WriteLine($"  commit:  {parts[1]}");
        }

        Console.WriteLine($"  runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  os:      {GetOsName()} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");

        // Assembly.Location is an empty string in a single-file app — which is how this ships — so
        // the executable's own path is the only reliable source for a build time.
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable) && File.Exists(executable))
        {
            Console.WriteLine($"  built:   {File.GetLastWriteTime(executable):yyyy-MM-dd HH:mm}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LanInspector CLI");
        Console.WriteLine();
        Console.WriteLine("Usage: laninspector <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  version                    Show the build this executable came from");
        Console.WriteLine("  status                     Show current network and Tailscale status");
        Console.WriteLine("  interfaces                 List network interfaces");
        Console.WriteLine("  known                      List known devices from config");
        Console.WriteLine("  locate [<id>] [--probe]    Find a known device's current LAN IP (alias: whereis)");
        Console.WriteLine("                             --json for machine-readable output");
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
        Console.WriteLine("  snmp discover                              Find which router answers SNMP");
        Console.WriteLine("  snmp <ip> --throughput [<seconds>]         Whole-network throughput at a router");
        Console.WriteLine("  flipper detect                             List serial ports, identify Flipper");
        Console.WriteLine("  flipper ports                              Print Flipper port name(s)");
        Console.WriteLine("  flipper info [--port <p>]                  Show connected Flipper firmware info");
        Console.WriteLine("  flipper subghz [--freq <mhz>] [--duration <sec>]  Sub-GHz IoT scan");
        Console.WriteLine("  flipper nfc [--timeout <sec>]             Detect NFC card");
        Console.WriteLine("  flipper rfid [--timeout <sec>]            Read 125 kHz RFID tag");
        Console.WriteLine("  flipper topology [--duration <sec>] [--json|--mermaid]  Topology with IoT");
        Console.WriteLine("  flipper cmd <command> [--timeout <sec>]            Raw CLI passthrough");
        Console.WriteLine();
        Console.WriteLine("Known device config is merged by device id from (later files win):");
        Console.WriteLine("  <exe-dir>/Data/known-devices.json, then known-devices.local.json");
        Console.WriteLine("  ~/.config/laninspector/known-devices.json, then known-devices.local.json");
        Console.WriteLine("  ./known-devices.json, then ./known-devices.local.json");
        Console.WriteLine();
        Console.WriteLine("No passwords are stored. SSH uses your local keys and ssh-agent.");
    }
}
