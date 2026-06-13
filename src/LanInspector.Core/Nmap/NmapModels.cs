using System.Net;

namespace LanInspector.Core.Nmap;

public enum NmapScanMode
{
    Ping,          // -sn
    TcpConnect,    // -sT --top-ports 100
    ServiceDetect  // -sV --version-light --top-ports 100
}

public sealed record NmapPort(int Number, string Protocol, string State, string Service, string? Version);

public sealed record NmapHost(
    IPAddress Address,
    string? Hostname,
    string Status,
    IReadOnlyList<NmapPort> Ports);

public sealed record NmapScanResult(
    DateTime StartedAt,
    DateTime FinishedAt,
    NmapScanMode Mode,
    IReadOnlyList<NmapHost> Hosts,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null;
}
