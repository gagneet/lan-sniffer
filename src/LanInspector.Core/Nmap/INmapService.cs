using System.Net;

namespace LanInspector.Core.Nmap;

public interface INmapService
{
    bool IsAvailable { get; }
    string? NmapPath { get; }
    Task<NmapScanResult> ScanAsync(string target, NmapScanMode mode, CancellationToken cancellationToken = default);
    Task<NmapScanResult> PingSweepAsync(string cidr, CancellationToken cancellationToken = default);
}
