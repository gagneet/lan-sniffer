using System.Net;

namespace LanInspector.Core.Snmp;

public sealed record SnmpDeviceInfo(
    IPAddress Address,
    string? SysDescr,
    string? SysName,
    string? SysLocation,
    IReadOnlyList<SnmpInterface> Interfaces,
    IReadOnlyList<string> IpAddresses);

public sealed record SnmpInterface(
    int Index,
    string Description,
    string Type,
    string OperStatus,
    long? SpeedBps);

public sealed record SnmpFdbEntry(string MacAddress, int Port);

/// <summary>
/// A point-in-time reading of one interface's byte counters.
/// </summary>
/// <param name="IsHighCapacity">
/// True when the 64-bit ifHC counters were available. The 32-bit fallbacks wrap at 4 GB, which at
/// 100 Mbps is under six minutes, so a reading taken from them is only trustworthy when polled
/// often and with wrap handled.
/// </param>
public sealed record SnmpInterfaceCounters(
    int Index,
    string Description,
    string OperStatus,
    long? SpeedBps,
    ulong InOctets,
    ulong OutOctets,
    bool IsHighCapacity,
    DateTimeOffset ReadAt);

public sealed record SnmpCountersResult(
    IPAddress Target,
    IReadOnlyList<SnmpInterfaceCounters> Interfaces,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null;
}

/// <summary>
/// Throughput derived from two counter readings of the same interface.
/// </summary>
public sealed record SnmpInterfaceThroughput(
    int Index,
    string Description,
    double InBytesPerSecond,
    double OutBytesPerSecond,
    TimeSpan Interval,
    long? SpeedBps)
{
    public double TotalBytesPerSecond => InBytesPerSecond + OutBytesPerSecond;

    /// <summary>Percentage of the interface's rated speed in use, when the speed is known.</summary>
    public double? UtilisationPercent => SpeedBps is > 0
        ? Math.Max(InBytesPerSecond, OutBytesPerSecond) * 8 / SpeedBps.Value * 100
        : null;
}

public sealed record SnmpQueryResult(
    IPAddress Target,
    SnmpDeviceInfo? Info,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null && Info is not null;
}
