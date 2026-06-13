using System.Net;
using System.Xml.Linq;
using LanInspector.Core.Diagnostics;

namespace LanInspector.Core.Nmap;

public sealed class NmapService : INmapService
{
    private static readonly string[] NmapPaths = OperatingSystem.IsWindows()
        ? [@"C:\Program Files (x86)\Nmap\nmap.exe", @"C:\Program Files\Nmap\nmap.exe"]
        : ["/usr/bin/nmap", "/usr/local/bin/nmap"];

    public bool IsAvailable => NmapPath is not null;

    public string? NmapPath { get; } = DetectNmap();

    public async Task<NmapScanResult> ScanAsync(string target, NmapScanMode mode, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Fail(mode, "nmap is not installed or not found in PATH");

        var args = BuildArguments(target, mode);
        var started = DateTime.UtcNow;

        try
        {
            var output = await ProcessHelper.RunAsync(NmapPath!, args, TimeSpan.FromMinutes(5), cancellationToken);
            var hosts = ParseXmlOutput(output);
            return new NmapScanResult(started, DateTime.UtcNow, mode, hosts);
        }
        catch (OperationCanceledException)
        {
            return Fail(mode, "Scan cancelled");
        }
        catch (Exception ex)
        {
            return Fail(mode, ex.Message);
        }
    }

    public Task<NmapScanResult> PingSweepAsync(string cidr, CancellationToken cancellationToken = default) =>
        ScanAsync(cidr, NmapScanMode.Ping, cancellationToken);

    private static string BuildArguments(string target, NmapScanMode mode)
    {
        var flags = mode switch
        {
            NmapScanMode.Ping => "-sn",
            NmapScanMode.TcpConnect => "-sT --top-ports 100",
            NmapScanMode.ServiceDetect => "-sV --version-light --top-ports 100",
            _ => "-sn"
        };
        return $"{flags} -oX - {target}";
    }

    private static IReadOnlyList<NmapHost> ParseXmlOutput(string xml)
    {
        var hosts = new List<NmapHost>();
        if (string.IsNullOrWhiteSpace(xml))
            return hosts;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return hosts; }

        foreach (var hostEl in doc.Root?.Elements("host") ?? [])
        {
            var status = hostEl.Element("status")?.Attribute("state")?.Value ?? "unknown";

            var addrEl = hostEl.Elements("address")
                .FirstOrDefault(a => a.Attribute("addrtype")?.Value == "ipv4");
            if (addrEl is null || !IPAddress.TryParse(addrEl.Attribute("addr")?.Value, out var ip))
                continue;

            var hostname = hostEl.Element("hostnames")?
                .Elements("hostname")
                .FirstOrDefault()?
                .Attribute("name")?.Value;

            var ports = new List<NmapPort>();
            foreach (var portEl in hostEl.Element("ports")?.Elements("port") ?? [])
            {
                if (!int.TryParse(portEl.Attribute("portid")?.Value, out var portId))
                    continue;

                var proto = portEl.Attribute("protocol")?.Value ?? "tcp";
                var state = portEl.Element("state")?.Attribute("state")?.Value ?? "unknown";
                var service = portEl.Element("service")?.Attribute("name")?.Value ?? "";
                var version = portEl.Element("service")?.Attribute("version")?.Value;

                ports.Add(new NmapPort(portId, proto, state, service, version));
            }

            hosts.Add(new NmapHost(ip, hostname, status, ports));
        }

        return hosts;
    }

    private static NmapScanResult Fail(NmapScanMode mode, string error) =>
        new(DateTime.UtcNow, DateTime.UtcNow, mode, [], error);

    private static string? DetectNmap()
    {
        if (ProcessHelper.IsAvailable("nmap"))
            return "nmap";

        return NmapPaths.FirstOrDefault(File.Exists);
    }
}
