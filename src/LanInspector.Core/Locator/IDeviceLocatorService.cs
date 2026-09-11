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

    /// <summary>Confirm candidates with a TCP connection before accepting them.</summary>
    public bool VerifyWithTcpProbe { get; init; } = true;

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(750);

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
