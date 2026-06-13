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

public sealed record SnmpQueryResult(
    IPAddress Target,
    SnmpDeviceInfo? Info,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null && Info is not null;
}
