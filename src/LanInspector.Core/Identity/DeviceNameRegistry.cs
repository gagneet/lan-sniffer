using System.Collections.Concurrent;
using System.Net;
using LanInspector.Core.Analysis;
using LanInspector.Core.Configuration;
using LanInspector.Core.Model;
using LanInspector.Core.Network;
using LanInspector.Core.RemoteAccess;

namespace LanInspector.Core.Identity;

/// <summary>
/// Turns an IP address into the most recognisable name available for it, pooling every source the
/// application already has: the known-device config, devices seen in the capture, this machine's
/// own interfaces, names learned from DNS and mDNS answers, and the tailnet peer list.
/// </summary>
/// <remarks>
/// Resolution is called once per row on every refresh of the traffic view, so the expensive part —
/// walking the known-device and captured-device collections — is done once and cached for a short
/// interval rather than per lookup.
/// </remarks>
public sealed class DeviceNameRegistry : IDeviceNameResolver
{
    /// <summary>
    /// Ceiling on remembered DNS names. A busy network resolves a great many hosts, and this map
    /// is a display convenience, not a record worth growing without bound.
    /// </summary>
    private const int MaxDnsNames = 4096;

    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<KnownDeviceDefinition> _knownDevices;
    private readonly Func<IEnumerable<Device>> _capturedDevices;
    private readonly ILocalNetworkProfileProvider? _profileProvider;
    private readonly ConcurrentDictionary<string, string> _dnsNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly object _snapshotGate = new();

    private Dictionary<string, string> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _snapshotTakenAt = DateTimeOffset.MinValue;
    private TailscaleStatus? _tailscale;

    public DeviceNameRegistry(
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        Func<IEnumerable<Device>>? capturedDevices = null,
        ILocalNetworkProfileProvider? profileProvider = null,
        TimeProvider? timeProvider = null)
    {
        _knownDevices = knownDevices;
        _capturedDevices = capturedDevices ?? Array.Empty<Device>;
        _profileProvider = profileProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Subscribes to an analyzer so DNS and mDNS answers feed the name map.</summary>
    public void Observe(DnsAnalyzer analyzer)
    {
        analyzer.NameObserved += OnNameObserved;
    }

    public void StopObserving(DnsAnalyzer analyzer)
    {
        analyzer.NameObserved -= OnNameObserved;
    }

    /// <summary>Supplies tailnet peer names, which label the 100.x addresses in captured traffic.</summary>
    public void SetTailscaleStatus(TailscaleStatus? status)
    {
        _tailscale = status;
        Invalidate();
    }

    /// <summary>Forces the next <see cref="Resolve"/> to rebuild, after a config or capture reset.</summary>
    public void Invalidate()
    {
        lock (_snapshotGate)
        {
            _snapshotTakenAt = DateTimeOffset.MinValue;
        }
    }

    public string? Resolve(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        var snapshot = GetSnapshot();
        if (snapshot.TryGetValue(ipAddress, out var name))
        {
            return name;
        }

        // DNS names are consulted after the snapshot so a configured or captured device keeps its
        // own label even when a DNS answer also pointed at that address.
        return _dnsNames.GetValueOrDefault(ipAddress);
    }

    /// <summary>
    /// Records a name-to-address mapping. Called for every DNS and mDNS answer once
    /// <see cref="Observe"/> has been wired up, and directly by anything else with a name to
    /// contribute.
    /// </summary>
    public void RecordDnsName(DnsNameObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.Name) || string.IsNullOrWhiteSpace(observation.IpAddress))
        {
            return;
        }

        // First name wins. A CDN address answers to many names, and re-labelling a row on every
        // refresh would make the traffic view flicker between them.
        if (_dnsNames.ContainsKey(observation.IpAddress) || _dnsNames.Count >= MaxDnsNames)
        {
            return;
        }

        _dnsNames.TryAdd(observation.IpAddress, observation.Name);
    }

    private void OnNameObserved(object? sender, DnsNameObservedEventArgs e) => RecordDnsName(e.Observation);

    private Dictionary<string, string> GetSnapshot()
    {
        lock (_snapshotGate)
        {
            if (_timeProvider.GetUtcNow() - _snapshotTakenAt < SnapshotLifetime)
            {
                return _snapshot;
            }

            _snapshot = BuildSnapshot();
            _snapshotTakenAt = _timeProvider.GetUtcNow();
            return _snapshot;
        }
    }

    /// <summary>
    /// Builds the address-to-name map lowest-priority source first, so that a later, better source
    /// overwrites an earlier one.
    /// </summary>
    private Dictionary<string, string> BuildSnapshot()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddTailscalePeers(names);
        AddCapturedDevices(names);
        AddKnownDevices(names);
        AddLocalInterfaces(names);

        return names;
    }

    private void AddTailscalePeers(Dictionary<string, string> names)
    {
        if (_tailscale is not { State: TailscaleConnectionState.Connected } status)
        {
            return;
        }

        foreach (var peer in status.Peers)
        {
            var label = string.IsNullOrWhiteSpace(peer.Name) ? peer.DnsName : peer.Name;
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            foreach (var address in peer.TailscaleIps)
            {
                names[address.ToString()] = label;
            }
        }

        foreach (var address in status.LocalIps)
        {
            names[address.ToString()] = "This machine";
        }
    }

    private void AddCapturedDevices(Dictionary<string, string> names)
    {
        foreach (var device in _capturedDevices())
        {
            string? label;
            string[] addresses;

            lock (device)
            {
                label = device.Hostname ?? device.ObservedNames.FirstOrDefault();
                addresses = [.. device.IpAddresses];
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            foreach (var address in addresses)
            {
                names[address] = label;
            }
        }
    }

    private void AddKnownDevices(Dictionary<string, string> names)
    {
        // Configured names outrank observed ones: "Home Server" is more use in a traffic row than
        // whatever hostname the DHCP lease happened to register.
        foreach (var known in _knownDevices)
        {
            if (string.IsNullOrWhiteSpace(known.DisplayName))
            {
                continue;
            }

            foreach (var address in known.KnownIps)
            {
                names[address] = known.DisplayName;
            }
        }

        // A captured device whose MAC matches a known device carries that name to whichever
        // address it currently holds, which is the whole point of tracking by MAC.
        foreach (var device in _capturedDevices())
        {
            string mac;
            string[] addresses;

            lock (device)
            {
                mac = device.MacAddress;
                addresses = [.. device.IpAddresses];
            }

            var match = _knownDevices.FirstOrDefault(known => known.MatchesMac(mac));
            if (match is null || string.IsNullOrWhiteSpace(match.DisplayName))
            {
                continue;
            }

            foreach (var address in addresses)
            {
                names[address] = match.DisplayName;
            }
        }
    }

    private void AddLocalInterfaces(Dictionary<string, string> names)
    {
        if (_profileProvider is null)
        {
            return;
        }

        LocalNetworkProfile profile;
        try
        {
            profile = _profileProvider.GetCurrentProfile();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (var localInterface in profile.Interfaces)
        {
            names[localInterface.Address.ToString()] = $"This machine ({localInterface.Name})";

            if (localInterface.GatewayAddress is not null)
            {
                var gateway = localInterface.GatewayAddress.ToString();

                // Only label the gateway when nothing better already names it — a configured
                // router entry says more than the word "Gateway".
                if (!names.ContainsKey(gateway))
                {
                    names[gateway] = "Gateway";
                }
            }
        }

        names[IPAddress.Broadcast.ToString()] = "Broadcast";
    }
}
