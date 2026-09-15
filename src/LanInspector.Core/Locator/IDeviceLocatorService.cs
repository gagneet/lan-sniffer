using LanInspector.Core.Configuration;

namespace LanInspector.Core.Locator;

public sealed record DeviceLocatorOptions
{
    public static DeviceLocatorOptions Default { get; } = new();

    /// <summary>
    /// Run <c>tailscale ping --until-direct</c> when cheaper sources produce nothing. This is the
    /// most reliable way to learn a peer's LAN address, but it can take several seconds per
    /// device, so it is opt-in for bulk lookups.
    /// </summary>
    public bool UseTailscalePingProbe { get; init; }

    /// <summary>
    /// Log in to the device over SSH and ask it for its own addresses, gateway and routers. Only
    /// tried for a device with an SSH profile, through its Tailscale address or a LAN address that
    /// has already accepted an SSH connection, and with keys only — never a password prompt. It is
    /// the one way to tell a device behind a NAT router from that router, and opt-in because it
    /// logs in to the device.
    /// </summary>
    public bool InspectOverSsh { get; init; }

    /// <summary>
    /// When a device's MAC is configured but not in the ARP cache, ping every address on this
    /// machine's own subnets and read the cache again. The operating system resolves each address
    /// it pings, so a device that took a new lease after a router or switch restart turns up by
    /// its MAC even when it answers nothing — no SSH, DNS or Tailscale needed. Only subnets of
    /// /22 or smaller are swept, and at most once per batch.
    /// </summary>
    public bool SweepLocalSubnets { get; init; }

    /// <summary>Confirm candidates with a TCP connection before accepting them.</summary>
    public bool VerifyWithTcpProbe { get; init; } = true;

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Ceiling on how many candidates are dialled. Candidates are probed in parallel and a device
    /// rarely has more than a handful, but a long history plus a busy tailnet could produce more.
    /// </summary>
    public int MaxCandidatesToProbe { get; init; } = 8;

    /// <summary>
    /// Fall back to an ICMP echo when no TCP port answers. Without it, a device that is present
    /// but has every probed port closed reports as unconfirmed.
    /// </summary>
    public bool VerifyWithIcmpFallback { get; init; } = true;

    /// <summary>Ports tried when a device has no SSH profile configured.</summary>
    public IReadOnlyList<int> FallbackProbePorts { get; init; } = [22, 80, 443];

    /// <summary>
    /// How long a remembered address stays worth trying. A recent observation outranks whatever
    /// is written in the config, because it is a real sighting rather than a guess — but an
    /// address last seen weeks ago is no longer evidence of anything, and should not keep beating
    /// a configured address the user has since corrected.
    /// </summary>
    public TimeSpan RememberedAddressMaxAge { get; init; } = TimeSpan.FromDays(7);
}

public interface IDeviceLocatorService
{
    Task<DeviceLocation> LocateAsync(
        KnownDeviceDefinition device,
        DeviceLocatorOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceLocation>> LocateAllAsync(
        IEnumerable<KnownDeviceDefinition> devices,
        DeviceLocatorOptions? options = null,
        CancellationToken cancellationToken = default);
}
