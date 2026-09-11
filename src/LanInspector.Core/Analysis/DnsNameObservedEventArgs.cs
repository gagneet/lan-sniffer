namespace LanInspector.Core.Analysis;

/// <summary>
/// A name-to-address mapping seen in a DNS or mDNS answer.
/// </summary>
/// <param name="IpAddress">The address the answer resolved to.</param>
/// <param name="Name">The record's name, with the trailing dot stripped.</param>
/// <param name="FromMulticast">True for mDNS (port 5353), which names devices on this LAN.</param>
public sealed record DnsNameObservation(string IpAddress, string Name, bool FromMulticast);

public sealed class DnsNameObservedEventArgs(DnsNameObservation observation) : EventArgs
{
    public DnsNameObservation Observation { get; } = observation;
}
