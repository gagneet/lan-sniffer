using System.Net;
using System.Text.RegularExpressions;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.Locator;

public sealed record ArpTableEntry(IPAddress Address, string NormalisedMac, string? InterfaceName, string? State);

public interface IArpTableReader
{
    Task<IReadOnlyList<ArpTableEntry>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the operating system's ARP / neighbour cache. The cache is the authoritative record of
/// which IP currently answers for which MAC on the local segment, so it is the strongest signal
/// available for "the server took a new DHCP lease — where is it now?".
/// </summary>
/// <remarks>
/// Only entries on the local layer-2 segment appear here. A device behind another router (the
/// 192.168.87.x side of a double-NAT, say) will not be listed no matter how reachable it is; the
/// locator falls back to Tailscale and DNS evidence for those.
/// </remarks>
public sealed partial class ArpTableReader : IArpTableReader
{
    private readonly Func<string, string, TimeSpan, CancellationToken, Task<string>> _runProcess;

    public ArpTableReader()
        : this(ProcessHelper.RunAsync)
    {
    }

    internal ArpTableReader(Func<string, string, TimeSpan, CancellationToken, Task<string>> runProcess)
    {
        _runProcess = runProcess;
    }

    public async Task<IReadOnlyList<ArpTableEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (fileName, arguments) in GetCommandsForPlatform())
        {
            var output = await _runProcess(fileName, arguments, TimeSpan.FromSeconds(5), cancellationToken);
            var entries = Parse(output);
            if (entries.Count > 0)
            {
                return entries;
            }
        }

        return [];
    }

    private static IEnumerable<(string FileName, string Arguments)> GetCommandsForPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("arp", "-a");
            yield break;
        }

        // "ip neigh" is preferred on Linux because it reports entry state (REACHABLE / STALE /
        // FAILED); "arp -an" is the fallback for minimal images and for macOS.
        if (OperatingSystem.IsLinux())
        {
            yield return ("ip", "neigh show");
        }

        yield return ("arp", "-an");
    }

    /// <summary>
    /// Parses any of the ARP table formats this application encounters by pulling the first IPv4
    /// address and the first MAC address out of each line. All four supported layouts put exactly
    /// one of each on a line, so a format-specific parser per platform would buy nothing.
    /// </summary>
    public static IReadOnlyList<ArpTableEntry> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var entries = new List<ArpTableEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var ipMatch = Ipv4Regex().Match(line);
            var macMatch = MacRegex().Match(line);
            if (!ipMatch.Success || !macMatch.Success)
            {
                continue;
            }

            if (!IPAddress.TryParse(ipMatch.Value, out var address))
            {
                continue;
            }

            var normalisedMac = MacAddressFormatter.Normalise(macMatch.Value);
            if (normalisedMac.Length != 12 || normalisedMac == "000000000000" || normalisedMac == "FFFFFFFFFFFF")
            {
                continue;
            }

            // Windows "arp -a" repeats the local interface address in an "Interface:" header and
            // each adapter's table follows; the header line carries no MAC so it is skipped above.
            if (!seen.Add($"{address}|{normalisedMac}"))
            {
                continue;
            }

            entries.Add(new ArpTableEntry(
                address,
                normalisedMac,
                InterfaceRegex().Match(line) is { Success: true } iface ? iface.Groups["name"].Value : null,
                StateRegex().Match(line) is { Success: true } state ? state.Value : null));
        }

        return entries;
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,2}[:-]){5}[0-9a-fA-F]{1,2}\b")]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"\b(?:dev|on)\s+(?<name>[A-Za-z0-9._-]+)")]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"\b(REACHABLE|STALE|DELAY|PROBE|PERMANENT|NOARP|INCOMPLETE|FAILED)\b")]
    private static partial Regex StateRegex();
}
