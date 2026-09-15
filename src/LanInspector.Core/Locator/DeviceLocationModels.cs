using System.Net;

namespace LanInspector.Core.Locator;

/// <summary>
/// How a candidate address for a device was obtained, ordered from strongest evidence to weakest.
/// The numeric order is meaningful: the locator prefers the lowest value that verifies.
/// </summary>
public enum LocationSource
{
    /// <summary>The MAC is in this machine's ARP cache, so the device is on this segment now.</summary>
    ArpTable = 0,

    /// <summary>The device itself, asked over SSH, reports holding this address.</summary>
    DeviceReported = 1,

    /// <summary>Tailscale has an active direct (non-relayed) path to the peer at this address.</summary>
    TailscaleDirectPath = 2,

    /// <summary>A <c>tailscale ping</c> probe established a direct path to this address.</summary>
    TailscalePing = 3,

    /// <summary>Tailscale lists this as a candidate endpoint or peer-API address for the peer.</summary>
    TailscaleEndpoint = 4,

    /// <summary>A hostname (DNS, MagicDNS or mDNS) resolved to this address.</summary>
    HostnameLookup = 5,

    /// <summary>The address recorded the last time this device was located.</summary>
    PreviousLocation = 6,

    /// <summary>A statically configured address from <c>known-devices.json</c>; may be stale.</summary>
    ConfiguredAddress = 7
}

public enum LocationConfidence
{
    /// <summary>A TCP connection to the device succeeded at this address.</summary>
    Confirmed,

    /// <summary>
    /// Strong evidence (ARP entry, live Tailscale path, or the device's own report) but no service
    /// probe succeeded.
    /// </summary>
    High,

    /// <summary>A single weaker signal, such as a candidate endpoint or a DNS answer.</summary>
    Medium,

    /// <summary>Configured or remembered only; nothing observed on the network.</summary>
    Low,

    /// <summary>No address could be determined at all.</summary>
    None
}

/// <summary>
/// How plausible it is that this machine could reach a candidate address at all, judged before
/// any packet is sent.
/// </summary>
/// <remarks>
/// This exists because Tailscale advertises every address a peer has, including its container
/// bridges. A server running Docker and Kubernetes offers up addresses like <c>10.20.4.1</c>
/// that are real on the peer and meaningless here; without this tier they would outrank the
/// peer's actual LAN address purely because endpoint evidence sits higher than configuration.
/// </remarks>
public enum CandidatePlausibility
{
    /// <summary>Inside one of this machine's own interface subnets — directly reachable.</summary>
    OnLocalSubnet = 0,

    /// <summary>Inside a subnet or address this device is configured to use — plausibly routed.</summary>
    ConfiguredForDevice = 1,

    /// <summary>On no subnet either machine is known to use — most likely a bridge on the peer.</summary>
    Unrelated = 2
}

public sealed record DeviceLocationCandidate(
    IPAddress Address,
    LocationSource Source,
    string Detail)
{
    /// <summary>Set once the candidate has been probed; null when it was never tested.</summary>
    public bool? IsVerified { get; init; }

    /// <summary>The TCP port that verified this candidate, when one did.</summary>
    public int? VerifiedPort { get; init; }

    /// <summary>
    /// True when nothing accepted a TCP connection but the host answered an ICMP echo. It proves
    /// the address is live without proving any service is up — enough for a device with no open
    /// ports, such as a Mac with Remote Login switched off.
    /// </summary>
    public bool VerifiedByIcmp { get; init; }

    public CandidatePlausibility Plausibility { get; init; } = CandidatePlausibility.Unrelated;
}

public sealed record DeviceLocation(
    string DeviceId,
    string DisplayName,
    IPAddress? CurrentAddress,
    LocationSource? Source,
    LocationConfidence Confidence,
    IReadOnlyList<DeviceLocationCandidate> Candidates,
    IReadOnlyList<string> Evidence)
{
    /// <summary>The Tailscale address, which does not change when the DHCP lease does.</summary>
    public IPAddress? TailscaleAddress { get; init; }

    public string? TailscaleName { get; init; }

    /// <summary>
    /// Every address that answered a probe, strongest evidence first, so
    /// <see cref="CurrentAddress"/> is the head of this list. A machine with a wired and a
    /// wireless interface on different subnets — common where a LAN switch and a mesh router
    /// both serve the same room — genuinely has more than one current address, and reporting
    /// only one hides the half the caller may actually be able to reach.
    /// </summary>
    public IReadOnlyList<IPAddress> VerifiedAddresses { get; init; } = [];

    public bool IsMultiHomed => VerifiedAddresses.Count > 1;

    /// <summary>Verified addresses other than the primary one.</summary>
    public IEnumerable<IPAddress> AdditionalAddresses => VerifiedAddresses.Skip(1);

    /// <summary>The address recorded by the previous locate, when it differs from the current one.</summary>
    public IPAddress? PreviousAddress { get; init; }

    /// <summary>When the address was first observed to differ from <see cref="PreviousAddress"/>.</summary>
    public DateTimeOffset? AddressChangedAt { get; init; }

    /// <summary>
    /// What the device said about its own network when asked over SSH: its addresses, its gateway
    /// and the routers beyond. Null when it was not asked or could not be reached.
    /// </summary>
    public DeviceNetworkReport? NetworkReport { get; init; }

    /// <summary>
    /// Where this machine's traffic to the device actually lands when that is not the device: the
    /// outside address of the NAT router it sits behind, as this network sees that router.
    /// </summary>
    public IPAddress? NatAddress { get; init; }

    /// <summary>Subnet routes the device advertises to Tailscale that have not been approved.</summary>
    public IReadOnlyList<string> UnapprovedRoutes { get; init; } = [];

    public bool HasMoved => PreviousAddress is not null
        && CurrentAddress is not null
        && !PreviousAddress.Equals(CurrentAddress);

    /// <summary>A one-line, plain-English answer to "where is this device right now?".</summary>
    public string Summary
    {
        get
        {
            if (CurrentAddress is null)
            {
                return TailscaleAddress is not null
                    ? $"{DisplayName} has no reachable LAN address; reach it over Tailscale at {TailscaleAddress}."
                    : $"{DisplayName} could not be located on this network.";
            }

            var via = Source is null ? string.Empty : $" ({Describe(Source.Value)})";
            var moved = HasMoved ? $" — changed from {PreviousAddress}" : string.Empty;
            var alsoAt = IsMultiHomed
                ? $" Also reachable at {string.Join(", ", AdditionalAddresses)}."
                : string.Empty;
            return $"{DisplayName} is at {CurrentAddress}{via}, confidence {Confidence}{moved}.{alsoAt}";
        }
    }

    public static string Describe(LocationSource source) => source switch
    {
        LocationSource.ArpTable => "ARP cache, matched by MAC",
        LocationSource.DeviceReported => "reported by the device over SSH",
        LocationSource.TailscaleDirectPath => "Tailscale direct path",
        LocationSource.TailscalePing => "Tailscale ping probe",
        LocationSource.TailscaleEndpoint => "Tailscale candidate endpoint",
        LocationSource.HostnameLookup => "hostname lookup",
        LocationSource.PreviousLocation => "remembered from last run",
        LocationSource.ConfiguredAddress => "configured address",
        _ => source.ToString()
    };
}
