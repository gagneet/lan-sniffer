using System.Net;

namespace LanInspector.Core.RemoteAccess;

public enum TailscaleConnectionState
{
    NotInstalled,
    InstalledNotConnected,
    Connected
}

public sealed record TailscaleDevice(
    string Name,
    string DnsName,
    IReadOnlyList<IPAddress> TailscaleIps,
    bool IsOnline);

public sealed record TailscaleStatus(
    TailscaleConnectionState State,
    IReadOnlyList<TailscaleDevice> Peers,
    IReadOnlyList<IPAddress> LocalIps,
    string? LocalName = null);
