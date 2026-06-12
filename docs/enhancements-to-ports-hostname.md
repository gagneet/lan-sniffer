**Short answer:** Use **reverse DNS + mDNS/SSDP/DHCP parsing** for hostnames, and an **OUI vendor database** for vendor names; to map devices to routers/switches combine **ARP/route inference + LLDP/CDP/SNMP** and optionally run targeted **Nmap** scans for open ports and sniffer detection. Below are concrete steps, code snippets, and recommended tools to add to your repo.

---

### Quick comparison: ways to associate a device to a router/switch

| **Method** | **What it shows** | **Accuracy** | **Requires** |
|---|---:|---:|---|
| ARP + routing table | Which gateway a host uses | Medium | Local capture + OS routing info |
| LLDP / CDP | Neighbor device and port info | High (managed gear) | LLDP/CDP packets or pktmon/pcap capture; admin rights |   [GitHub](https://github.com/gavc/clldp)
| DHCP server logs | Which DHCP server leased IP | High | Access to DHCP server or capture DHCP packets |
| SNMP queries | Switch MAC table, port mapping | High | SNMP read access to switches/routers |
| Active traceroute/probes | Path to gateway | Medium | ICMP/TCP probes; may be blocked |

---

## How to get **Hostnames**
1. **Passive DNS parsing** — parse DNS responses you capture (PacketDotNet + SharpPcap) and map IP→name from answers. **SharpPcap** is the recommended capture library for .NET.   [GitHub](https://github.com/OverTM/dotpcap.sharppcap)  
2. **Reverse DNS lookup** (fallback): `Dns.GetHostEntry(ip)` in C# (async, with timeouts).  
3. **mDNS / SSDP / NetBIOS / DHCP**: parse mDNS and SSDP packets to get friendly names; parse DHCP `hostname` and `vendor-class` options from DHCP packets captured on the wire.

**C# example (reverse DNS):**
```csharp
var entry = await Dns.GetHostEntryAsync(ipAddress);
device.Hostname = entry.HostName;
```

**mDNS/DHCP parsing**: subscribe analyzers in your packet pipeline (PacketDotNet + custom parsers).

---

## How to get **Vendor (OUI)**
- Use an OUI/vendor DB (Wireshark `manuf` or maintained JSON/CSV repos). Reuse a C# library such as **MacAddressVendorLookup** or import a JSON OUI file and match the first 3 bytes of the MAC.   [GitHub](https://github.com/PingmanTools/MacAddressVendorLookup)  [GitHub](https://github.com/uxmansarwar/mac-address-vendor-database)

**C# sketch:**
```csharp
var vendor = MacVendorLookup.Find(macAddress);
device.Vendor = vendor?.Organization ?? "Unknown";
```

---

## Mapping device → router/switch (practical recipe)
- **Read local routing table** to find default gateway; correlate ARP entries to MACs seen.  
- **Capture LLDP/CDP** frames (or use pktmon-based tools) to learn switch port and neighbor info when available.   [GitHub](https://github.com/gavc/clldp)  
- **If switches are managed**, query SNMP to read CAM tables and map MAC→port.  
- **If you control the network**, enable port mirroring or deploy lightweight agents on each segment to centralize visibility.

---

## Open ports, services, and sniffer detection
- **Integrate Nmap** for targeted scans (call nmap and parse XML with `NmapXmlParser` or use a .NET wrapper). Use light scans (top ports) to avoid disruption.   [Libraries.io](https://libraries.io/nuget/NmapXmlParser)  [GitHub](https://github.com/coj337/Nmap.NET)  
- **Detect sniffers/promiscuous NICs**: run Nmap’s `sniffer-detect` NSE or implement ARP‑based promiscuous tests; monitor ARP anomalies and unexpected ARP replies.   [InfosecMatter](https://www.infosecmatter.com/nmap-nse-library/?nse=sniffer-detect)  [secra.es](https://secra.es/en/blog/what-is-a-sniffer-network-traffic-analysis)

---

## Extra telemetry to collect
- TLS SNI and HTTP Host headers (metadata only), DHCP vendor class, mDNS service types, SSDP device types, NetBIOS/SMB names, traffic volumes and top talkers, and periodic LLDP snapshots.

---

---------------------------------------------

**generate the exact C# classes and analyzers** for DNS/mDNS/DHCP parsing and show how to call Nmap and parse results into your `Device` model. Which one should I produce first: **DNS/mDNS analyzer** or **Nmap integration + XML parser**?

---

### DNS / mDNS / DHCP analyzers and Nmap integration (C#)

Below are ready‑to‑drop C# classes and supporting code you can add to your `LanInspector.Core` project. They:

- **Parse DNS (UDP 53)** and **mDNS (UDP 5353)** to extract hostnames and map IP ↔ name.  
- **Parse DHCP (UDP 67/68)** to extract DHCP hostname and vendor class.  
- **Update your `Device` model** with hostnames, vendor strings, and open ports.  
- **Run Nmap** as an external process and parse its XML output to populate `Device.OpenPorts`.  

Use these as a starting point — they are intentionally pragmatic and minimal so you can extend them (more DNS record types, full DHCP option parsing, DNS name compression handling, etc.).

---

### 1) Add fields to `Device` model

```csharp
// LanInspector.Core.Model.Device.cs
using System;
using System.Collections.Generic;

namespace LanInspector.Core.Model
{
    public class Device
    {
        public string MacAddress { get; set; }
        public HashSet<string> IpAddresses { get; } = new();
        public string Hostname { get; set; }
        public string Vendor { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsGateway { get; set; }
        public bool IsAccessPoint { get; set; }

        // New fields
        public HashSet<int> OpenPorts { get; } = new();
        public Dictionary<int, string> PortServices { get; } = new();
        public List<string> ObservedDnsNames { get; } = new();
        public string DhcpVendorClass { get; set; }
    }
}
```

---

### 2) DNS analyzer (UDP 53) — basic parser for queries/responses

> **Notes:** This parser handles common DNS response formats and extracts A/AAAA/CNAME answers and query names. It does not implement every DNS compression edge case; extend as needed.

```csharp
// LanInspector.Core.Analysis.DnsAnalyzer.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using PacketDotNet;
using LanInspector.Core.Model;

namespace LanInspector.Core.Analysis
{
    public class DnsAnalyzer : IPacketAnalyzer
    {
        private readonly ConcurrentDictionary<string, Device> _devices;

        public DnsAnalyzer(ConcurrentDictionary<string, Device> devices)
        {
            _devices = devices;
        }

        public void Analyze(Packet packet)
        {
            var udp = packet.Extract<UdpPacket>();
            if (udp == null) return;

            int srcPort = udp.SourcePort;
            int dstPort = udp.DestinationPort;

            // DNS standard port 53 and mDNS 5353
            if (srcPort != 53 && dstPort != 53 && srcPort != 5353 && dstPort != 5353) return;

            var ip = packet.Extract<IPv4Packet>() ?? (Packet)packet.Extract<IPv6Packet>();
            if (ip == null) return;

            var payload = udp.PayloadData;
            if (payload == null || payload.Length < 12) return; // DNS header 12 bytes

            try
            {
                var dns = SimpleDnsParser.Parse(payload);
                // For each answer A/AAAA, map IP -> name
                foreach (var ans in dns.Answers)
                {
                    if (ans.Type == DnsRecordType.A || ans.Type == DnsRecordType.AAAA)
                    {
                        var ipStr = ans.Data;
                        var name = ans.Name?.TrimEnd('.');
                        if (IPAddress.TryParse(ipStr, out var ipAddr))
                        {
                            // find device by IP or create by MAC unknown
                            foreach (var kv in _devices)
                            {
                                var dev = kv.Value;
                                if (dev.IpAddresses.Contains(ipStr))
                                {
                                    dev.Hostname ??= name;
                                    dev.ObservedDnsNames.Add(name);
                                    dev.LastSeen = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }

                // For queries, we can map source IP -> queried name (useful)
                foreach (var q in dns.Questions)
                {
                    var qname = q.Name?.TrimEnd('.');
                    var srcIp = ip.SourceAddress.ToString();
                    foreach (var kv in _devices)
                    {
                        var dev = kv.Value;
                        if (dev.IpAddresses.Contains(srcIp))
                        {
                            if (!string.IsNullOrEmpty(qname))
                            {
                                dev.ObservedDnsNames.Add(qname);
                                if (string.IsNullOrEmpty(dev.Hostname))
                                    dev.Hostname = qname;
                                dev.LastSeen = DateTime.UtcNow;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
        }
    }

    // Minimal DNS parser types
    public enum DnsRecordType { A = 1, AAAA = 28, CNAME = 5, PTR = 12, TXT = 16 }

    public class DnsQuestion { public string Name; public ushort Type; public ushort Class; }
    public class DnsAnswer { public string Name; public ushort Type; public ushort Class; public uint Ttl; public string Data; }

    public class SimpleDnsMessage
    {
        public List<DnsQuestion> Questions { get; } = new();
        public List<DnsAnswer> Answers { get; } = new();
    }

    public static class SimpleDnsParser
    {
        // Very small parser: reads header, questions, answers; handles A/AAAA/CNAME basic RDATA
        public static SimpleDnsMessage Parse(byte[] data)
        {
            var msg = new SimpleDnsMessage();
            int pos = 0;
            if (data.Length < 12) throw new ArgumentException("Not DNS");
            // header
            pos += 4; // ID + flags
            ushort qdcount = ReadUShort(data, ref pos);
            ushort ancount = ReadUShort(data, ref pos);
            pos += 4; // nscount + arcount

            for (int i = 0; i < qdcount; i++)
            {
                var name = ReadName(data, ref pos);
                ushort type = ReadUShort(data, ref pos);
                ushort cls = ReadUShort(data, ref pos);
                msg.Questions.Add(new DnsQuestion { Name = name, Type = type, Class = cls });
            }

            for (int i = 0; i < ancount; i++)
            {
                var name = ReadName(data, ref pos);
                ushort type = ReadUShort(data, ref pos);
                ushort cls = ReadUShort(data, ref pos);
                uint ttl = ReadUInt(data, ref pos);
                ushort rdlength = ReadUShort(data, ref pos);
                string rdata = "";

                if (type == (ushort)DnsRecordType.A && rdlength == 4)
                {
                    rdata = $"{data[pos]}.{data[pos+1]}.{data[pos+2]}.{data[pos+3]}";
                }
                else if (type == (ushort)DnsRecordType.AAAA && rdlength == 16)
                {
                    var parts = new List<string>();
                    for (int k = 0; k < 8; k++)
                    {
                        ushort part = (ushort)((data[pos + k*2] << 8) | data[pos + k*2 + 1]);
                        parts.Add(part.ToString("x"));
                    }
                    rdata = string.Join(":", parts);
                }
                else if (type == (ushort)DnsRecordType.CNAME || type == (ushort)DnsRecordType.PTR)
                {
                    int tmp = pos;
                    rdata = ReadName(data, ref tmp);
                }
                else
                {
                    // skip or store raw
                    rdata = BitConverter.ToString(data, pos, rdlength);
                }

                pos += rdlength;
                msg.Answers.Add(new DnsAnswer { Name = name, Type = type, Class = cls, Ttl = ttl, Data = rdata });
            }

            return msg;
        }

        private static ushort ReadUShort(byte[] data, ref int pos)
        {
            ushort v = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;
            return v;
        }

        private static uint ReadUInt(byte[] data, ref int pos)
        {
            uint v = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            pos += 4;
            return v;
        }

        private static string ReadName(byte[] data, ref int pos)
        {
            var labels = new List<string>();
            int jumped = -1;
            int originalPos = pos;
            while (pos < data.Length)
            {
                byte len = data[pos++];
                if (len == 0) break;
                // pointer
                if ((len & 0xC0) == 0xC0)
                {
                    byte b2 = data[pos++];
                    int pointer = ((len & 0x3F) << 8) | b2;
                    if (jumped == -1) jumped = pos;
                    pos = pointer;
                    continue;
                }
                if (pos + len > data.Length) break;
                labels.Add(System.Text.Encoding.ASCII.GetString(data, pos, len));
                pos += len;
            }
            if (jumped != -1) pos = jumped;
            return string.Join(".", labels);
        }
    }
}
```

---

### 3) mDNS analyzer

mDNS uses the same DNS format but on UDP port 5353 and multicast address. The `DnsAnalyzer` above already checks port 5353; you can treat mDNS names specially (strip `.local` and prefer them for hostnames). No separate class required unless you want service parsing (e.g., `_http._tcp.local`).

If you want a dedicated mDNS analyzer to extract service types:

```csharp
// LanInspector.Core.Analysis.MdnsAnalyzer.cs
// Wraps DnsAnalyzer logic and extracts service types from Questions/Answers
// Example: if answer.Name ends with "_http._tcp.local", add service to device metadata.
```

---

### 4) DHCP analyzer (UDP 67/68) — extract hostname (option 12) and vendor class (option 60)

> **Notes:** DHCP is BOOTP-based. This parser extracts the DHCP options block and looks for option 12 (hostname) and 60 (vendor class). It assumes IPv4 and UDP payload layout.

```csharp
// LanInspector.Core.Analysis.DhcpAnalyzer.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PacketDotNet;
using LanInspector.Core.Model;

namespace LanInspector.Core.Analysis
{
    public class DhcpAnalyzer : IPacketAnalyzer
    {
        private readonly ConcurrentDictionary<string, Device> _devices;

        public DhcpAnalyzer(ConcurrentDictionary<string, Device> devices)
        {
            _devices = devices;
        }

        public void Analyze(Packet packet)
        {
            var udp = packet.Extract<UdpPacket>();
            if (udp == null) return;
            if (!(udp.SourcePort == 67 || udp.DestinationPort == 67 || udp.SourcePort == 68 || udp.DestinationPort == 68)) return;

            var ip = packet.Extract<IPv4Packet>();
            if (ip == null) return;

            var payload = udp.PayloadData;
            if (payload == null || payload.Length < 240) return; // BOOTP header + magic cookie

            // BOOTP: op(1), htype(1), hlen(1), hops(1), xid(4), secs(2), flags(2), ciaddr(4), yiaddr(4), siaddr(4), giaddr(4), chaddr(16), sname(64), file(128)
            int pos = 240; // options start after 240 bytes (BOOTP fixed)
            // check magic cookie 99,130,83,99
            if (payload[236] != 99 || payload[237] != 130 || payload[238] != 83 || payload[239] != 99) return;

            string hostname = null;
            string vendorClass = null;

            while (pos < payload.Length)
            {
                byte opt = payload[pos++];
                if (opt == 255) break; // end
                if (opt == 0) continue; // pad
                if (pos >= payload.Length) break;
                byte len = payload[pos++];
                if (pos + len > payload.Length) break;
                if (opt == 12) // Host Name
                {
                    hostname = System.Text.Encoding.ASCII.GetString(payload, pos, len).Trim('\0');
                }
                else if (opt == 60) // Vendor class identifier
                {
                    vendorClass = System.Text.Encoding.ASCII.GetString(payload, pos, len).Trim('\0');
                }
                pos += len;
            }

            // Map to device by client hardware address (chaddr at offset 28, length hlen)
            var macBytes = new byte[6];
            Array.Copy(payload, 28, macBytes, 0, 6);
            var mac = BitConverter.ToString(macBytes).Replace("-", "");

            var device = _devices.GetOrAdd(mac, _ => new Device
            {
                MacAddress = mac,
                FirstSeen = DateTime.UtcNow
            });

            if (!string.IsNullOrEmpty(hostname))
            {
                device.Hostname ??= hostname;
                device.ObservedDnsNames.Add(hostname);
            }

            if (!string.IsNullOrEmpty(vendorClass))
            {
                device.DhcpVendorClass = vendorClass;
            }

            device.LastSeen = DateTime.UtcNow;
        }
    }
}
```

---

### 5) OUI / vendor lookup helper

Load an OUI CSV (first 3 bytes → vendor) into memory and lookup by MAC prefix.

```csharp
// LanInspector.Core.Util.MacVendorLookup.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LanInspector.Core.Util
{
    public static class MacVendorLookup
    {
        private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        // Call once at startup with path to OUI file (CSV or "XX-XX-XX,Vendor")
        public static void LoadFromCsv(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var prefix = parts[0].Replace(":", "").Replace("-", "").Trim().ToUpper();
                if (prefix.Length >= 6) prefix = prefix.Substring(0, 6);
                var vendor = parts[1].Trim();
                _map[prefix] = vendor;
            }
        }

        public static string Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            var clean = mac.Replace(":", "").Replace("-", "").Trim().ToUpper();
            if (clean.Length < 6) return null;
            var prefix = clean.Substring(0, 6);
            return _map.TryGetValue(prefix, out var v) ? v : null;
        }
    }
}
```

**Where to get OUI file:** export from IEEE or use Wireshark `manuf` file converted to CSV. Load it at app startup: `MacVendorLookup.LoadFromCsv("data/oui.csv");`

---

### 6) Nmap integration — run Nmap and parse XML output

> **Prerequisites:** Nmap must be installed on the machine and available in PATH. Use light scans (e.g., `-F` or `--top-ports`) to avoid long scans. This code runs Nmap as a process and parses the XML output into `Device.OpenPorts`.

```csharp
// LanInspector.Core.Integration.NmapRunner.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using LanInspector.Core.Model;

namespace LanInspector.Core.Integration
{
    public class NmapRunner
    {
        private readonly ConcurrentDictionary<string, Device> _devices;

        public NmapRunner(ConcurrentDictionary<string, Device> devices)
        {
            _devices = devices;
        }

        // Run a quick scan on a single IP and parse results
        public async Task RunQuickScanAsync(string ip, int timeoutMs = 60000)
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nmap",
                    Arguments = $"-oX \"{tmpFile}\" -Pn -sT --top-ports 100 {ip}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Failed to start nmap");
                var finished = proc.WaitForExit(timeoutMs);
                if (!finished)
                {
                    try { proc.Kill(); } catch { }
                    throw new TimeoutException("Nmap timed out");
                }

                // parse XML
                var doc = XDocument.Load(tmpFile);
                ParseNmapXml(doc, ip);
            }
            finally
            {
                try { File.Delete(tmpFile); } catch { }
            }
        }

        private void ParseNmapXml(XDocument doc, string targetIp)
        {
            // nmap xml structure: <nmaprun><host><address addr="x.x.x.x" addrtype="ipv4"/><ports><port protocol="tcp" portid="22"><state state="open"/><service name="ssh"/></port>...
            var hosts = doc.Descendants("host");
            foreach (var host in hosts)
            {
                var addrElem = host.Element("address");
                if (addrElem == null) continue;
                var addr = addrElem.Attribute("addr")?.Value;
                if (addr != targetIp) continue;

                var ports = host.Descendants("port");
                foreach (var p in ports)
                {
                    var portId = int.Parse(p.Attribute("portid").Value);
                    var state = p.Element("state")?.Attribute("state")?.Value;
                    var svc = p.Element("service")?.Attribute("name")?.Value;

                    if (state == "open" || state == "open|filtered")
                    {
                        // find device by IP
                        foreach (var kv in _devices)
                        {
                            var dev = kv.Value;
                            if (dev.IpAddresses.Contains(addr))
                            {
                                dev.OpenPorts.Add(portId);
                                if (!string.IsNullOrEmpty(svc))
                                    dev.PortServices[portId] = svc;
                                dev.LastSeen = DateTime.UtcNow;
                            }
                        }
                    }
                }
            }
        }
    }
}
```

**Usage:** call `await nmapRunner.RunQuickScanAsync("192.168.0.12");` from a background task or UI command. Parse results will update `Device.OpenPorts`.

---

### 7) Wiring analyzers into capture pipeline

Add analyzers to your capture provider event handler:

```csharp
// Example wiring in some CaptureService
private readonly ConcurrentDictionary<string, Device> _devices = new();
private readonly List<IPacketAnalyzer> _analyzers;

public CaptureService()
{
    _analyzers = new List<IPacketAnalyzer>
    {
        new ArpAnalyzer(_devices),
        new DnsAnalyzer(_devices),
        new DhcpAnalyzer(_devices),
        // add MdnsAnalyzer if separate
    };
}

private void OnPacketCaptured(object sender, PacketCapturedEventArgs e)
{
    var packet = e.ParsedPacket;
    foreach (var analyzer in _analyzers)
    {
        try { analyzer.Analyze(packet); }
        catch { /* log */ }
    }
}
```

---

### 8) Inferring which router/switch a device is connected to

Use these signals (combine heuristics):

- **Default gateway mapping:** read OS routing table to get default gateway IP and MAC. Devices whose ARP traffic targets that gateway likely use that router.
  ```csharp
  var gateways = NetworkInterface.GetAllNetworkInterfaces()
      .SelectMany(n => n.GetIPProperties().GatewayAddresses)
      .Where(g => g.Address != null)
      .Select(g => g.Address.ToString()).ToList();
  ```
- **DHCP server:** if DHCP packets show `siaddr` or `server identifier` option, that IP is the DHCP server — devices with leases from that server are on that router.
- **ARP traffic direction:** if you see ARP requests from a device for addresses in a particular subnet, infer its local segment.
- **LLDP/CDP:** capture LLDP (EtherType 0x88cc) and CDP (EtherType 0x2000) frames; if present, they reveal neighbor relationships and switch port info. Add a small LLDP parser to extract `chassis id` and `port id`.
- **SNMP (optional):** if you have SNMP read access to routers/switches, query the bridge MIB (`dot1dTpFdbTable`) to map MAC → port and `ifDescr` to map port → interface name.

**Practical approach:** combine DHCP lease server, ARP gateway, and LLDP/CDP when available. Present a confidence score (high if SNMP/LLDP present, medium if DHCP/gateway match, low if only ARP).

---

### 9) Detecting sniffers / promiscuous NICs (basic)

- **Nmap NSE `sniffer-detect`** can be used; integrate via Nmap runner.  
- **ARP-based heuristic:** send a gratuitous ARP for a non-existent IP and see if any host responds with a MAC that is not the owner — indicates ARP spoofing.  
- **Traffic anomalies:** devices that see traffic for many IPs (many ARP replies, many DNS queries for other hosts) may be in promiscuous mode or be a router.

Be careful: active tests can be disruptive; always run with caution on production networks.

---

### 10) Putting it together in UI

- **Device row**: show `Hostname` (prefer DHCP > mDNS > reverse DNS > observed DNS), `Vendor` (OUI), `IPs`, `OpenPorts` (count), `LastSeen`, `Role` (inferred).
- **Device detail**: show `ObservedDnsNames`, `DhcpVendorClass`, `PortServices` (from Nmap), `Traffic graph`.
- **Topology view**: show routers/gateways as nodes, connect devices to gateway inferred by DHCP/ARP/LLDP. Show confidence color (green/yellow/red).

---

### 11) Practical tips and caveats

- **Permissions:** capturing packets and sending crafted packets may require admin privileges and Npcap installed with appropriate options.  
- **Windows Wi‑Fi monitor mode:** Windows Wi‑Fi drivers often do not support true monitor mode; you will see only traffic to/from your adapter and broadcast/multicast. For full visibility, use a mirrored port or a sensor on each segment.  
- **Nmap caution:** scanning can trigger IDS/IPS or upset other users. Use `-Pn` and light scans for discovery; ask permission on networks you don’t own.  
- **Performance:** keep analyzers efficient; use `ConcurrentDictionary` and background tasks for heavy work (Nmap, reverse DNS lookups).  
- **Rate limiting:** do reverse DNS lookups and Nmap scans with throttling and timeouts to avoid blocking UI.

---

### 12) Next steps for the code to be produced

- **A.** Full `MainViewModel` wiring that starts capture, registers analyzers, and updates an `ObservableCollection<Device>` for the UI.  
- **B.** LLDP/CDP parser class to improve topology inference.  
- **C.** SNMP helper (walk `dot1dTpFdbTable`) with example queries (requires SNMP credentials).  
- **D.** A small sample plugin that registers the DNS/DHCP analyzers via your plugin manager.


