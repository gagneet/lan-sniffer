using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanInspector.Core.Configuration;
using LanInspector.Core.Diagnostics;
using LanInspector.Core.Identity;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;
using LanInspector.Core.Scanning;

namespace LanInspector.Core.Locator;

/// <summary>
/// Answers "which IP address is this known device on right now?" for a network where DHCP hands
/// out a different lease after every power cycle.
/// </summary>
/// <remarks>
/// <para>
/// Evidence is gathered from every source that is cheap enough to consult, ranked by
/// <see cref="LocationSource"/>, and then probed: the first candidate that accepts a TCP
/// connection wins. If nothing accepts a connection, the strongest unverified candidate is
/// returned and marked as such, so the caller can show a best guess rather than nothing.
/// </para>
/// <para>
/// The Tailscale address is reported separately and always, because it is the one address that
/// does not move — for reaching the device it is strictly better than any LAN address, and the
/// LAN address matters mostly for local-only services and for understanding the network.
/// </para>
/// </remarks>
public sealed class DeviceLocatorService : IDeviceLocatorService
{
    private readonly ITailscaleService _tailscale;
    private readonly ILocalNetworkProfileProvider _profileProvider;
    private readonly IArpTableReader _arpTableReader;
    private readonly PortScanner _portScanner;
    private readonly HostnameResolver _hostnameResolver;
    private readonly DeviceLocationHistoryStore? _history;
    private readonly TimeProvider _timeProvider;

    public DeviceLocatorService(
        ITailscaleService tailscale,
        ILocalNetworkProfileProvider profileProvider,
        IArpTableReader arpTableReader,
        PortScanner portScanner,
        HostnameResolver hostnameResolver,
        DeviceLocationHistoryStore? history = null,
        TimeProvider? timeProvider = null)
    {
        _tailscale = tailscale;
        _profileProvider = profileProvider;
        _arpTableReader = arpTableReader;
        _portScanner = portScanner;
        _hostnameResolver = hostnameResolver;
        _history = history;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DeviceLocation> LocateAsync(
        KnownDeviceDefinition device,
        DeviceLocatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(cancellationToken);
        return await LocateAsync(device, context, options ?? DeviceLocatorOptions.Default, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceLocation>> LocateAllAsync(
        IEnumerable<KnownDeviceDefinition> devices,
        DeviceLocatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The ARP table and Tailscale status are shared across every device in the batch, so they
        // are read once rather than once per device.
        var context = await BuildContextAsync(cancellationToken);
        var effectiveOptions = options ?? DeviceLocatorOptions.Default;

        var results = new List<DeviceLocation>();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await LocateAsync(device, context, effectiveOptions, cancellationToken));
        }

        return results;
    }

    private async Task<LocatorContext> BuildContextAsync(CancellationToken cancellationToken)
    {
        var arpEntries = await SafeReadArpAsync(cancellationToken);
        var tailscale = await SafeGetTailscaleStatusAsync(cancellationToken);
        return new LocatorContext(arpEntries, tailscale, _profileProvider.GetCurrentProfile());
    }

    private async Task<DeviceLocation> LocateAsync(
        KnownDeviceDefinition device,
        LocatorContext context,
        DeviceLocatorOptions options,
        CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        var candidates = new Dictionary<string, DeviceLocationCandidate>(StringComparer.OrdinalIgnoreCase);

        var peer = context.Tailscale.State == TailscaleConnectionState.Connected
            ? context.Tailscale.FindPeerByName(device.KnownTailscaleNames.Concat(device.KnownHostnames).Append(device.Id))
            : null;

        AddArpCandidates(device, context, candidates, evidence);
        AddTailscaleCandidates(peer, candidates, evidence);

        if (options.UseTailscalePingProbe && peer is not null)
        {
            await AddTailscalePingCandidateAsync(peer, candidates, evidence, cancellationToken);
        }

        await AddHostnameCandidatesAsync(device, peer, candidates, evidence, cancellationToken);
        AddRememberedCandidate(device, options, candidates, evidence);
        AddConfiguredCandidates(device, candidates, evidence);

        // Plausibility outranks evidence strength. A Tailscale endpoint list is strong evidence
        // about the peer but says nothing about what this machine can reach, and on a host running
        // Docker or Kubernetes it is mostly container bridges.
        var ordered = candidates.Values
            .Select(candidate => candidate with { Plausibility = RatePlausibility(candidate.Address, context, device) })
            .OrderBy(candidate => candidate.Plausibility)
            .ThenBy(candidate => candidate.Source)
            .ToList();

        foreach (var unrelated in ordered.Where(candidate => candidate.Plausibility == CandidatePlausibility.Unrelated))
        {
            evidence.Add($"{unrelated.Address} is on no subnet this machine or {device.Id} is known to use — treated as a low-priority guess (a container bridge on the peer would look like this).");
        }

        var probePorts = GetProbePorts(device, options);
        var probed = options.VerifyWithTcpProbe
            ? await ProbeAsync(ordered, probePorts, options, evidence, cancellationToken)
            : ordered;

        var verified = probed.Where(candidate => candidate.IsVerified == true).ToArray();
        var winner = verified.FirstOrDefault() ?? probed.FirstOrDefault();

        if (verified.Length > 1)
        {
            evidence.Add($"This device answers on {verified.Length} addresses ({string.Join(", ", verified.Select(candidate => candidate.Address))}); it has more than one active interface.");
        }

        var confidence = DetermineConfidence(winner);
        var tailscaleAddress = peer?.TailscaleIps.FirstOrDefault(RouteHelpers.IsCgnatOrTailscale)
            ?? peer?.TailscaleIps.FirstOrDefault();

        if (peer is null && device.KnownTailscaleNames.Count > 0
            && context.Tailscale.State == TailscaleConnectionState.Connected)
        {
            evidence.Add($"No Tailscale peer matched {string.Join(", ", device.KnownTailscaleNames)}.");
        }
        else if (context.Tailscale.State != TailscaleConnectionState.Connected)
        {
            evidence.Add($"Tailscale is {context.Tailscale.State}; overlay evidence unavailable.");
        }

        var record = winner is not null && _history is not null
            ? _history.Record(device.Id, winner.Address, winner.Source, _timeProvider.GetUtcNow())
            : _history?.Get(device.Id);

        IPAddress? previousAddress = null;
        if (record?.PreviousAddress is not null && IPAddress.TryParse(record.PreviousAddress, out var parsedPrevious))
        {
            previousAddress = parsedPrevious;
        }

        return new DeviceLocation(
            device.Id,
            string.IsNullOrWhiteSpace(device.DisplayName) ? device.Id : device.DisplayName,
            winner?.Address,
            winner?.Source,
            confidence,
            probed,
            evidence)
        {
            TailscaleAddress = tailscaleAddress,
            TailscaleName = peer?.DnsName is { Length: > 0 } dnsName ? dnsName : peer?.Name,
            VerifiedAddresses = [.. verified.Select(candidate => candidate.Address)],
            PreviousAddress = previousAddress,
            // Only meaningful alongside a previous address; on a first sighting the store still
            // stamps a time, but reporting it would imply a move that never happened.
            AddressChangedAt = previousAddress is null ? null : record?.ChangedAt
        };
    }

    private static void AddArpCandidates(
        KnownDeviceDefinition device,
        LocatorContext context,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence)
    {
        if (device.KnownMacs.Count == 0)
        {
            evidence.Add("No MAC configured for this device, so the ARP cache cannot identify it. Add \"knownMacs\" for the most reliable tracking.");
            return;
        }

        var wanted = device.NormalisedMacs;
        var matches = context.ArpEntries.Where(entry => wanted.Contains(entry.NormalisedMac)).ToArray();

        if (matches.Length == 0)
        {
            evidence.Add("Device MAC is not in this machine's ARP cache; it is not on this layer-2 segment (or has not spoken recently).");
            return;
        }

        foreach (var match in matches)
        {
            var state = match.State is null ? string.Empty : $", state {match.State}";
            var iface = match.InterfaceName is null ? string.Empty : $" on {match.InterfaceName}";
            AddCandidate(candidates, new DeviceLocationCandidate(
                match.Address,
                LocationSource.ArpTable,
                $"ARP cache{iface}: {MacAddressFormatter.ToDisplayForm(match.NormalisedMac)} -> {match.Address}{state}"));

            evidence.Add($"ARP cache maps {MacAddressFormatter.ToDisplayForm(match.NormalisedMac)} to {match.Address}{state}.");
        }
    }

    private static void AddTailscaleCandidates(
        TailscaleDevice? peer,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence)
    {
        if (peer is null)
        {
            return;
        }

        evidence.Add($"Tailscale peer '{peer.Name}' is {(peer.IsOnline ? "online" : "offline")}.");

        if (peer.CurrentAddress is not null && RouteHelpers.IsRfc1918(peer.CurrentAddress.Address))
        {
            AddCandidate(candidates, new DeviceLocationCandidate(
                peer.CurrentAddress.Address,
                LocationSource.TailscaleDirectPath,
                $"Tailscale is sending packets directly to {peer.CurrentAddress}"));

            evidence.Add($"Tailscale has a direct path to the peer at {peer.CurrentAddress}.");
        }

        foreach (var address in peer.LanAddressCandidates)
        {
            AddCandidate(candidates, new DeviceLocationCandidate(
                address,
                LocationSource.TailscaleEndpoint,
                $"Tailscale lists {address} as an endpoint for '{peer.Name}'"));
        }
    }

    private async Task AddTailscalePingCandidateAsync(
        TailscaleDevice peer,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence,
        CancellationToken cancellationToken)
    {
        var target = !string.IsNullOrWhiteSpace(peer.DnsName) ? peer.DnsName : peer.Name;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        IPEndPoint? endpoint;
        try
        {
            endpoint = await _tailscale.TryGetDirectEndpointAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            evidence.Add($"Tailscale ping probe failed: {ex.Message}");
            return;
        }

        if (endpoint is null)
        {
            evidence.Add("Tailscale ping did not establish a direct path (relayed via DERP), so it revealed no LAN address.");
            return;
        }

        if (!RouteHelpers.IsRfc1918(endpoint.Address))
        {
            evidence.Add($"Tailscale ping took a direct path via {endpoint}, which is not a private address — the peer is not on a LAN shared with this machine.");
            return;
        }

        AddCandidate(candidates, new DeviceLocationCandidate(
            endpoint.Address,
            LocationSource.TailscalePing,
            $"tailscale ping reached the peer directly at {endpoint}"));

        evidence.Add($"tailscale ping established a direct path to {endpoint}.");
    }

    private async Task AddHostnameCandidatesAsync(
        KnownDeviceDefinition device,
        TailscaleDevice? peer,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence,
        CancellationToken cancellationToken)
    {
        var names = new List<string>(device.KnownHostnames);
        if (peer?.Name is { Length: > 0 } peerName)
        {
            names.Add(peerName);
        }

        // A bare hostname resolves through the router's DHCP-registered names; the ".local" form
        // goes to mDNS, which is what answers on segments with no DNS registration.
        var expanded = names
            .SelectMany(name => name.Contains('.') ? new[] { name } : new[] { name, $"{name}.local" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var name in expanded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var addresses = await _hostnameResolver.ResolveIpv4Async(name, TimeSpan.FromSeconds(2));
            foreach (var address in addresses.Where(RouteHelpers.IsRfc1918))
            {
                AddCandidate(candidates, new DeviceLocationCandidate(
                    address,
                    LocationSource.HostnameLookup,
                    $"'{name}' resolved to {address}"));

                evidence.Add($"Hostname '{name}' resolves to {address}.");
            }
        }
    }

    private void AddRememberedCandidate(
        KnownDeviceDefinition device,
        DeviceLocatorOptions options,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence)
    {
        var record = _history?.Get(device.Id);
        if (record?.LastAddress is null || !IPAddress.TryParse(record.LastAddress, out var address))
        {
            return;
        }

        var age = record.ObservedAt is null ? (TimeSpan?)null : _timeProvider.GetUtcNow() - record.ObservedAt.Value;
        if (age > options.RememberedAddressMaxAge)
        {
            evidence.Add($"Last located at {address} {age.Value.TotalDays:F0} days ago — too stale to try.");
            return;
        }

        AddCandidate(candidates, new DeviceLocationCandidate(
            address,
            LocationSource.PreviousLocation,
            $"Last located at {address}{(record.ObservedAt is null ? string.Empty : $" on {record.ObservedAt:u}")}"));

        evidence.Add($"Previously located at {address}{(record.ObservedAt is null ? string.Empty : $" ({record.ObservedAt:u})")}.");
    }

    private static void AddConfiguredCandidates(
        KnownDeviceDefinition device,
        Dictionary<string, DeviceLocationCandidate> candidates,
        List<string> evidence)
    {
        foreach (var configured in device.KnownIps)
        {
            if (IPAddress.TryParse(configured, out var address))
            {
                AddCandidate(candidates, new DeviceLocationCandidate(
                    address,
                    LocationSource.ConfiguredAddress,
                    $"Configured in known-devices.json as {address}"));
            }
        }

        if (device.KnownIps.Count > 0)
        {
            evidence.Add($"Configured addresses: {string.Join(", ", device.KnownIps)}.");
        }
    }

    /// <summary>
    /// Keeps the strongest source for each address. The same address routinely arrives from
    /// several sources at once (ARP cache and Tailscale endpoint list, say) and should be probed
    /// and reported once, attributed to its best evidence.
    /// </summary>
    private static void AddCandidate(Dictionary<string, DeviceLocationCandidate> candidates, DeviceLocationCandidate candidate)
    {
        var key = candidate.Address.ToString();
        if (!candidates.TryGetValue(key, out var existing) || candidate.Source < existing.Source)
        {
            candidates[key] = candidate;
        }
    }

    private static CandidatePlausibility RatePlausibility(
        IPAddress address,
        LocatorContext context,
        KnownDeviceDefinition device)
    {
        if (context.Profile.FindLocalInterface(address) is not null)
        {
            return CandidatePlausibility.OnLocalSubnet;
        }

        if (device.KnownIps.Any(ip => string.Equals(ip, address.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return CandidatePlausibility.ConfiguredForDevice;
        }

        foreach (var subnet in device.KnownSubnets)
        {
            if (IPv4Network.TryParse(subnet, out var network) && network!.Contains(address))
            {
                return CandidatePlausibility.ConfiguredForDevice;
            }
        }

        // A /24 around a configured address counts too, so that a device whose lease moved within
        // its own subnet is not demoted just because the exact address changed.
        foreach (var configured in device.KnownIps)
        {
            if (IPAddress.TryParse(configured, out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork
                && IPv4Network.FromAddressAndPrefix(parsed, 24).Contains(address))
            {
                return CandidatePlausibility.ConfiguredForDevice;
            }
        }

        return CandidatePlausibility.Unrelated;
    }

    private static IReadOnlyList<int> GetProbePorts(KnownDeviceDefinition device, DeviceLocatorOptions options)
    {
        return device.Ssh?.Enabled == true && device.Ssh.Port > 0
            ? [device.Ssh.Port]
            : options.FallbackProbePorts;
    }

    /// <summary>
    /// Dials every candidate rather than stopping at the first that answers. A dual-homed machine
    /// — wired on one subnet, wireless on another — answers on both, and stopping early would
    /// report one address while leaving the other marked "not probed", which is exactly backwards
    /// for the caller deciding which one their own machine can reach. Candidates are probed
    /// concurrently, so the extra coverage costs roughly one probe timeout, not one per address.
    /// </summary>
    private async Task<IReadOnlyList<DeviceLocationCandidate>> ProbeAsync(
        IReadOnlyList<DeviceLocationCandidate> candidates,
        IReadOnlyList<int> ports,
        DeviceLocatorOptions options,
        List<string> evidence,
        CancellationToken cancellationToken)
    {
        var toProbe = candidates.Take(options.MaxCandidatesToProbe).ToArray();
        var notProbed = candidates.Skip(options.MaxCandidatesToProbe).ToArray();

        var probed = await Task.WhenAll(toProbe.Select(async candidate =>
        {
            int? openPort = null;
            foreach (var port in ports)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _portScanner.ScanPortAsync(candidate.Address, port, options.ProbeTimeout, cancellationToken);
                if (result.IsOpen)
                {
                    openPort = port;
                    break;
                }
            }

            if (openPort is not null)
            {
                return candidate with { IsVerified = true, VerifiedPort = openPort };
            }

            // No port answered. A device can still be present with every port closed — a Mac with
            // Remote Login off, a printer, an appliance — so fall back to ICMP before writing the
            // address off.
            var answeredPing = options.VerifyWithIcmpFallback
                && await TryPingAsync(candidate.Address, options.ProbeTimeout, cancellationToken);

            return candidate with { IsVerified = answeredPing, VerifiedByIcmp = answeredPing };
        }));

        // Evidence is added after the parallel probes so its order follows candidate rank rather
        // than whichever probe happened to finish first.
        foreach (var candidate in probed.Where(candidate => candidate.IsVerified == true))
        {
            evidence.Add(candidate.VerifiedByIcmp
                ? $"{candidate.Address} answered an ICMP echo, but no probed TCP port was open."
                : $"TCP port {candidate.VerifiedPort} accepted a connection at {candidate.Address}.");
        }

        if (candidates.Count > 0 && !probed.Any(candidate => candidate.IsVerified == true))
        {
            evidence.Add($"No candidate accepted a connection on port(s) {string.Join(", ", ports)}; the reported address is unconfirmed.");
        }

        if (notProbed.Length > 0)
        {
            evidence.Add($"{notProbed.Length} lower-ranked candidate(s) were not probed (limit {options.MaxCandidatesToProbe}).");
        }

        return [.. probed, .. notProbed];
    }

    private static LocationConfidence DetermineConfidence(DeviceLocationCandidate? winner)
    {
        if (winner is null)
        {
            return LocationConfidence.None;
        }

        if (winner.IsVerified == true)
        {
            // An open TCP port proves a service answered there; an echo reply only proves the
            // address is live, which is one step weaker.
            return winner.VerifiedByIcmp ? LocationConfidence.High : LocationConfidence.Confirmed;
        }

        return winner.Source switch
        {
            LocationSource.ArpTable => LocationConfidence.High,
            LocationSource.TailscaleDirectPath => LocationConfidence.High,
            LocationSource.TailscalePing => LocationConfidence.High,
            LocationSource.TailscaleEndpoint => LocationConfidence.Medium,
            LocationSource.HostnameLookup => LocationConfidence.Medium,
            _ => LocationConfidence.Low
        };
    }

    private static async Task<bool> TryPingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or SocketException or OperationCanceledException)
        {
            // ICMP is frequently blocked outright, and unprivileged ICMP is unavailable on some
            // platforms. Either way this is a failed probe, not an error worth surfacing.
            return false;
        }
    }

    private async Task<IReadOnlyList<ArpTableEntry>> SafeReadArpAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _arpTableReader.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<TailscaleStatus> SafeGetTailscaleStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _tailscale.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TailscaleStatus(TailscaleConnectionState.NotInstalled, [], []);
        }
    }

    private sealed record LocatorContext(
        IReadOnlyList<ArpTableEntry> ArpEntries,
        TailscaleStatus Tailscale,
        LocalNetworkProfile Profile);
}
