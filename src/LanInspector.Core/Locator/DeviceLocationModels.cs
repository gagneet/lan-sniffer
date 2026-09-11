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

    /// <summary>Tailscale has an active direct (non-relayed) path to the peer at this address.</summary>
    TailscaleDirectPath = 1,

    /// <summary>A <c>tailscale ping</c> probe established a direct path to this address.</summary>
    TailscalePing = 2,

    /// <summary>Tailscale lists this as a candidate endpoint or peer-API address for the peer.</summary>
    TailscaleEndpoint = 3,

    /// <summary>A hostname (DNS, MagicDNS or mDNS) resolved to this address.</summary>
    HostnameLookup = 4,

    /// <summary>The address recorded the last time this device was located.</summary>
    PreviousLocation = 5,

    /// <summary>A statically configured address from <c>known-devices.json</c>; may be stale.</summary>
    ConfiguredAddress = 6
}

public enum LocationConfidence
{
    /// <summary>A TCP connection to the device succeeded at this address.</summary>
    Confirmed,

    /// <summary>Strong evidence (ARP entry or live Tailscale path) but no service probe succeeded.</summary>
    High,

    /// <summary>A single weaker signal, such as a candidate endpoint or a DNS answer.</summary>
    Medium,

    /// <summary>Configured or remembered only; nothing observed on the network.</summary>
    Low,

    /// <summary>No address could be determined at all.</summary>
    None
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

    /// <summary>The address recorded by the previous locate, when it differs from the current one.</summary>
    public IPAddress? PreviousAddress { get; init; }

    /// <summary>When the address was first observed to differ from <see cref="PreviousAddress"/>.</summary>
    public DateTimeOffset? AddressChangedAt { get; init; }

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
            return $"{DisplayName} is at {CurrentAddress}{via}, confidence {Confidence}{moved}.";
        }
    }

    public static string Describe(LocationSource source) => source switch
    {
        LocationSource.ArpTable => "ARP cache, matched by MAC",
        LocationSource.TailscaleDirectPath => "Tailscale direct path",
        LocationSource.TailscalePing => "Tailscale ping probe",
        LocationSource.TailscaleEndpoint => "Tailscale candidate endpoint",
        LocationSource.HostnameLookup => "hostname lookup",
        LocationSource.PreviousLocation => "remembered from last run",
        LocationSource.ConfiguredAddress => "configured address",
        _ => source.ToString()
    };
}
