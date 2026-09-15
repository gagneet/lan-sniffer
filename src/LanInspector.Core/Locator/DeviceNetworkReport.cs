using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Network;

namespace LanInspector.Core.Locator;

/// <summary>An address a device holds, as the device itself reports it.</summary>
public sealed record ReportedInterface(string Name, IPAddress Address, int PrefixLength, string? Mac)
{
    public IPv4Network? Network => Address.AddressFamily == AddressFamily.InterNetwork
        ? IPv4Network.FromAddressAndPrefix(Address, PrefixLength)
        : null;
}

/// <summary>
/// What a device says about its own place on the network, gathered by logging in to it: the
/// addresses it holds, the router it sends traffic to, and the routers beyond that one.
/// </summary>
/// <remarks>
/// <para>
/// Asking the device is the only dependable way to learn which router it hangs off. Seen from the
/// far side of a NAT router, everything about a device behind it — the address traffic reaches,
/// even Tailscale's direct path to it — is really the router's outside address, and nothing
/// observable from there tells the two apart.
/// </para>
/// <para>
/// Unmanaged switches never appear. They have no address and take no part in routing, so a device
/// plugged into one reports the router beyond it as its gateway.
/// </para>
/// </remarks>
public sealed record DeviceNetworkReport(
    string? Hostname,
    string? OperatingSystem,
    IReadOnlyList<ReportedInterface> Interfaces,
    IPAddress? Gateway,
    string? GatewayInterface,
    string? GatewayMac,
    IReadOnlyList<IPAddress> UpstreamHops,
    IReadOnlyList<string> AdvertisedRoutes)
{
    /// <summary>The gateway's manufacturer, from its MAC, when the vendor list knows it.</summary>
    public string? GatewayVendor { get; init; }

    /// <summary>The IPv4 interface carrying the default route: the one actually on the network.</summary>
    public ReportedInterface? PrimaryInterface =>
        Interfaces.FirstOrDefault(item => item.Name == GatewayInterface && item.Address.AddressFamily == AddressFamily.InterNetwork)
        ?? Interfaces.FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>
    /// Routers from the device outward, its own gateway first. A trace normally starts at the
    /// gateway, but one that ignores trace probes is still the first router.
    /// </summary>
    public IReadOnlyList<IPAddress> RouterChain
    {
        get
        {
            var gateway = Gateway;
            if (gateway is null || (UpstreamHops.Count > 0 && UpstreamHops[0].Equals(gateway)))
            {
                return UpstreamHops.Count > 0 || gateway is null ? UpstreamHops : [gateway];
            }

            return [gateway, .. UpstreamHops.Where(hop => !hop.Equals(gateway))];
        }
    }

    public bool HasAddress(IPAddress address) => Interfaces.Any(item => item.Address.Equals(address));

    /// <summary>
    /// One line, from the device outward:
    /// <c>enp2s0 192.168.0.148/24 -> 192.168.0.1 (Vendor) -> 192.168.87.1</c>.
    /// </summary>
    public string DescribePath()
    {
        var parts = new List<string>();
        if (PrimaryInterface is { } primary)
        {
            parts.Add($"{primary.Name} {primary.Address}/{primary.PrefixLength}");
        }

        foreach (var router in RouterChain)
        {
            parts.Add(router.Equals(Gateway) && !string.IsNullOrWhiteSpace(GatewayVendor)
                ? $"{router} ({GatewayVendor})"
                : router.ToString());
        }

        return string.Join(" -> ", parts);
    }
}

/// <summary>
/// Reads the output of <see cref="SshNetworkInspector"/>'s script: marked sections holding the
/// device's own network commands — <c>ip</c> on Linux, <c>ifconfig</c> and <c>route</c> on macOS
/// and the BSDs.
/// </summary>
public static partial class DeviceNetworkReportParser
{
    internal const string SectionMarker = "### ";

    // Container, VPN and virtual interfaces. Their addresses are real on the device and unreachable
    // from anywhere else; a Docker bridge reported as "the server's address" is exactly the mistake
    // this list exists to prevent.
    private static readonly string[] VirtualInterfacePrefixes =
    [
        "lo", "docker", "br-", "veth", "cali", "cni", "flannel", "vxlan", "virbr", "kube", "cilium", "lxc", "lxd",
        "tailscale", "utun", "tun", "tap", "wg", "zt", "vmnet", "vboxnet", "bridge", "awdl", "llw", "anpi", "gif", "stf"
    ];

    private static readonly IPv4Network LinkLocalV4 = IPv4Network.FromAddressAndPrefix(IPAddress.Parse("169.254.0.0"), 16);

    private static readonly byte[] TailscaleIpv6Prefix = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    public static DeviceNetworkReport Parse(string? output)
    {
        var sections = SplitSections(output);

        var interfaces = sections.ContainsKey("ip-addr")
            ? ParseIpAddr(sections["ip-addr"], ParseIpLink(sections.GetValueOrDefault("ip-link")))
            : ParseIfconfig(sections.GetValueOrDefault("ifconfig"));

        var (gateway, gatewayInterface) = sections.ContainsKey("ip-route")
            ? ParseIpRoute(sections["ip-route"])
            : ParseRouteGet(sections.GetValueOrDefault("route-get"));

        var gatewayMac = gateway is null
            ? null
            : ArpTableReader.Parse(sections.GetValueOrDefault("neigh")).FirstOrDefault(entry => entry.Address.Equals(gateway))?.NormalisedMac;

        return new DeviceNetworkReport(
            FirstLine(sections.GetValueOrDefault("host")),
            FirstLine(sections.GetValueOrDefault("os")),
            interfaces.Where(IsReachableFromOutside).ToArray(),
            gateway,
            gatewayInterface,
            gatewayMac is null ? null : MacAddressFormatter.ToDisplayForm(gatewayMac),
            ParseTrace(sections.GetValueOrDefault("trace")),
            ParseAdvertisedRoutes(sections.GetValueOrDefault("tailscale-prefs")));
    }

    private static Dictionary<string, string> SplitSections(string? output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var body = new StringBuilder();

        foreach (var line in Lines(output))
        {
            if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections[current] = body.ToString();
                }

                current = line[SectionMarker.Length..].Trim();
                body.Clear();
                continue;
            }

            body.Append(line).Append('\n');
        }

        if (current is not null)
        {
            sections[current] = body.ToString();
        }

        return sections;
    }

    private static bool IsReachableFromOutside(ReportedInterface item)
    {
        if (VirtualInterfacePrefixes.Any(prefix => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || IPAddress.IsLoopback(item.Address))
        {
            return false;
        }

        if (item.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !LinkLocalV4.Contains(item.Address) && !RouteHelpers.IsCgnatOrTailscale(item.Address);
        }

        return !item.Address.IsIPv6LinkLocal
            && !item.Address.IsIPv6Multicast
            && !item.Address.GetAddressBytes().AsSpan(0, TailscaleIpv6Prefix.Length).SequenceEqual(TailscaleIpv6Prefix);
    }

    /// <summary>
    /// IPv6 temporary addresses rotate within hours, and deprecated or tentative ones cannot be used;
    /// none of them says where a device lives.
    /// </summary>
    private static bool IsShortLived(string flags) =>
        flags.Contains("temporary", StringComparison.Ordinal)
        || flags.Contains("deprecated", StringComparison.Ordinal)
        || flags.Contains("tentative", StringComparison.Ordinal);

    // "2: enp2s0    inet 192.168.0.148/24 brd 192.168.0.255 scope global dynamic enp2s0\  valid_lft ..."
    [GeneratedRegex(@"^\d+:\s+(?<name>\S+)\s+inet6?\s+(?<address>[0-9A-Fa-f:.]+)/(?<prefix>\d{1,3})(?<flags>.*)$")]
    private static partial Regex IpAddrRegex();

    // "2: enp2s0: <BROADCAST,...> mtu 1500 ... link/ether 68:1d:ef:3c:d5:45 brd ff:ff:ff:ff:ff:ff"
    [GeneratedRegex(@"^\d+:\s+(?<name>[^:@\s]+)(?:@\S+)?:.*?\blink/ether\s+(?<mac>(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2})")]
    private static partial Regex IpLinkRegex();

    [GeneratedRegex(@"^default\s+via\s+(?<gateway>\S+)(?:.*?\bdev\s+(?<device>\S+))?")]
    private static partial Regex IpRouteRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9._-]+):\s+flags=")]
    private static partial Regex IfconfigHeaderRegex();

    [GeneratedRegex(@"^\s+ether\s+(?<mac>(?:[0-9A-Fa-f]{1,2}:){5}[0-9A-Fa-f]{1,2})\b")]
    private static partial Regex IfconfigEtherRegex();

    // macOS writes the mask in hex ("netmask 0xffffff00"); Linux net-tools writes it dotted.
    [GeneratedRegex(@"^\s+inet\s+(?<address>\d{1,3}(?:\.\d{1,3}){3})\s+netmask\s+(?<mask>0x[0-9A-Fa-f]{8}|\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex IfconfigInetRegex();

    [GeneratedRegex(@"^\s+inet6\s+(?<address>[0-9A-Fa-f:]+)(?:%\S+)?\s+prefixlen\s+(?<prefix>\d{1,3})(?<flags>.*)$")]
    private static partial Regex IfconfigInet6Regex();

    // traceroute: " 1  192.168.0.1  0.192 ms"    tracepath: " 1:  192.168.0.1   0.515ms"
    [GeneratedRegex(@"^\s*(?<hop>\d+):?\s+(?<address>\d{1,3}(?:\.\d{1,3}){3})\b")]
    private static partial Regex TraceHopRegex();

    [GeneratedRegex(@"""(?<route>[0-9A-Fa-f:.]+/\d{1,3})""")]
    private static partial Regex RouteRegex();

    private static Dictionary<string, string> ParseIpLink(string? section)
    {
        var macs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(section))
        {
            if (IpLinkRegex().Match(line) is { Success: true } match)
            {
                macs[match.Groups["name"].Value] = MacAddressFormatter.ToDisplayForm(match.Groups["mac"].Value);
            }
        }

        return macs;
    }

    private static List<ReportedInterface> ParseIpAddr(string section, IReadOnlyDictionary<string, string> macs)
    {
        var result = new List<ReportedInterface>();
        foreach (var line in Lines(section))
        {
            var match = IpAddrRegex().Match(line);
            if (!match.Success
                || IsShortLived(match.Groups["flags"].Value)
                || !IPAddress.TryParse(match.Groups["address"].Value, out var address)
                || !int.TryParse(match.Groups["prefix"].Value, out var prefix))
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            result.Add(new ReportedInterface(name, address, prefix, macs.GetValueOrDefault(name)));
        }

        return result;
    }

    private static List<ReportedInterface> ParseIfconfig(string? section)
    {
        var result = new List<ReportedInterface>();
        var addresses = new List<(IPAddress Address, int Prefix)>();
        string? name = null;
        string? mac = null;

        // The MAC can come before or after the addresses in a block, so a block is only turned into
        // interfaces once it ends.
        void EndBlock()
        {
            var blockName = name;
            var blockMac = mac;
            if (blockName is not null)
            {
                result.AddRange(addresses.Select(item => new ReportedInterface(blockName, item.Address, item.Prefix, blockMac)));
            }

            addresses.Clear();
            mac = null;
        }

        foreach (var line in Lines(section))
        {
            if (IfconfigHeaderRegex().Match(line) is { Success: true } header)
            {
                EndBlock();
                name = header.Groups["name"].Value;
            }
            else if (IfconfigEtherRegex().Match(line) is { Success: true } ether)
            {
                mac = MacAddressFormatter.ToDisplayForm(ether.Groups["mac"].Value);
            }
            else if (IfconfigInetRegex().Match(line) is { Success: true } inet
                && IPAddress.TryParse(inet.Groups["address"].Value, out var v4)
                && TryParseMask(inet.Groups["mask"].Value, out var v4Prefix))
            {
                addresses.Add((v4, v4Prefix));
            }
            else if (IfconfigInet6Regex().Match(line) is { Success: true } inet6
                && !IsShortLived(inet6.Groups["flags"].Value)
                && IPAddress.TryParse(inet6.Groups["address"].Value, out var v6)
                && int.TryParse(inet6.Groups["prefix"].Value, out var v6Prefix))
            {
                addresses.Add((v6, v6Prefix));
            }
        }

        EndBlock();
        return result;
    }

    private static bool TryParseMask(string mask, out int prefix)
    {
        prefix = 0;
        uint bits;

        if (mask.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(mask[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits))
            {
                return false;
            }
        }
        else if (IPAddress.TryParse(mask, out var dotted) && dotted.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = dotted.GetAddressBytes();
            bits = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
        else
        {
            return false;
        }

        prefix = BitOperations.PopCount(bits);
        return true;
    }

    private static (IPAddress? Gateway, string? Interface) ParseIpRoute(string section)
    {
        foreach (var line in Lines(section))
        {
            var match = IpRouteRegex().Match(line.Trim());
            if (match.Success && IPAddress.TryParse(match.Groups["gateway"].Value, out var gateway))
            {
                return (gateway, match.Groups["device"].Success ? match.Groups["device"].Value : null);
            }
        }

        return (null, null);
    }

    // route -n get default:  "    gateway: 192.168.0.1"  "  interface: en0"
    private static (IPAddress? Gateway, string? Interface) ParseRouteGet(string? section)
    {
        IPAddress? gateway = null;
        string? name = null;

        foreach (var line in Lines(section))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("gateway:", StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(trimmed["gateway:".Length..].Trim(), out var parsed))
            {
                gateway = parsed;
            }
            else if (trimmed.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed["interface:".Length..].Trim();
            }
        }

        return (gateway, name);
    }

    /// <summary>
    /// Private-address hops in order, up to the first public one: the routers inside the site.
    /// Beyond that is the ISP, which says nothing about which router the device sits behind.
    /// </summary>
    private static IReadOnlyList<IPAddress> ParseTrace(string? section)
    {
        var hops = new List<IPAddress>();
        var seenHops = new HashSet<int>();

        foreach (var line in Lines(section))
        {
            var match = TraceHopRegex().Match(line);

            // tracepath prints the first hop twice; the hop number is what identifies a hop.
            if (!match.Success
                || !int.TryParse(match.Groups["hop"].Value, out var hop)
                || !seenHops.Add(hop)
                || !IPAddress.TryParse(match.Groups["address"].Value, out var address))
            {
                continue;
            }

            if (!RouteHelpers.IsRfc1918(address))
            {
                break;
            }

            hops.Add(address);
        }

        return hops;
    }

    private static IReadOnlyList<string> ParseAdvertisedRoutes(string? section)
    {
        // A default route is how a device offers itself as an exit node, which is not a subnet.
        return RouteRegex().Matches(section ?? string.Empty)
            .Select(match => match.Groups["route"].Value)
            .Where(route => route is not "0.0.0.0/0" and not "::/0")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FirstLine(string? section) =>
        Lines(section).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r'));
}
