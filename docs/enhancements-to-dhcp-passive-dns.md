I checked the linked GitHub repo metadata and it looks like the repository is currently empty / not yet pushed with source code, so I cannot review the actual implementation yet. Based on your screenshot, though, the first version is already doing the right initial thing: capturing packets and building a MAC/IP table. The next improvements should be **device identity**, **reachability**, **service discovery**, and **topology inference**.

One important correction first: **do not map every IP packet’s source/destination IP to the Ethernet MAC you see in the packet.** That only works reliably for same-subnet traffic. For routed traffic, the Ethernet MAC may be the router/gateway MAC, not the real remote device MAC. For accurate IP-to-MAC mapping, prefer:

```text
ARP packets
DHCP packets
Router DHCP lease tables
mDNS/SSDP metadata
Same-subnet packets only
```

For your case, `192.168.87.xxx` devices may be reachable from `192.168.0.75`, but they are not necessarily directly ARP-visible from the `192.168.0.0/24` side.

---

## 1. How to populate Vendor

Vendor comes from the device MAC address using an **OUI lookup**.

Example:

```text
MAC: 3C-61-05-CD-80-75
OUI: 3C-61-05
Vendor: usually looked up from IEEE / Wireshark manuf / local OUI database
```

Add a local OUI database file to your app, for example:

```text
Data/oui.csv
```

Format:

```csv
Prefix,Vendor
3C6105,Google LLC
50-02-91,Google LLC
44-AD-B1,Sagemcom Broadband SAS
```

Then normalise MAC addresses before lookup.

### C# MAC vendor lookup

```csharp
public sealed class OuiVendorLookup
{
    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);

    public void LoadCsv(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(',', 2);
            if (parts.Length < 2)
                continue;

            var prefix = NormalizePrefix(parts[0]);
            var vendor = parts[1].Trim();

            if (!string.IsNullOrWhiteSpace(prefix) && !_vendors.ContainsKey(prefix))
                _vendors[prefix] = vendor;
        }
    }

    public string? LookupVendor(string macAddress)
    {
        var normalized = NormalizeMac(macAddress);

        if (normalized.Length < 6)
            return null;

        var oui24 = normalized[..6];

        if (_vendors.TryGetValue(oui24, out var vendor))
            return vendor;

        return IsLocallyAdministeredMac(normalized)
            ? "Private/randomized MAC"
            : null;
    }

    private static string NormalizePrefix(string value)
    {
        return new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static string NormalizeMac(string value)
    {
        return new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static bool IsLocallyAdministeredMac(string normalizedMac)
    {
        if (normalizedMac.Length < 2)
            return false;

        var firstByte = Convert.ToByte(normalizedMac[..2], 16);

        // Locally administered bit is bit 1 of first octet.
        return (firstByte & 0x02) == 0x02;
    }
}
```

In your screenshot, this MAC is suspicious:

```text
A4-CF-12-B4-D5-D4
```

You should identify whether it is a real vendor OUI or a private/randomised MAC. Many phones, tablets and laptops use random/private MAC addresses on Wi-Fi, so the vendor lookup may be blank or misleading.

---

## 2. How to populate Hostname

Hostname is harder than Vendor. There is no single guaranteed method. You need multiple resolvers and then merge the results.

Use these in order:

| Method                  | Best for                            | Reliability                               |
| ----------------------- | ----------------------------------- | ----------------------------------------- |
| Reverse DNS             | Routers/DHCP with DNS records       | Medium                                    |
| mDNS `.local`           | Apple, Google, Linux, IoT, printers | High for modern devices                   |
| NetBIOS/NBNS            | Windows devices                     | Medium                                    |
| DHCP sniffing           | Device names during lease request   | High, but only when DHCP packets are seen |
| SSDP/UPnP               | TVs, routers, media devices         | Medium/high                               |
| Router DHCP lease table | Best if accessible                  | High                                      |
| Manual alias            | Best long-term                      | Highest                                   |

### Reverse DNS resolver

```csharp
public static class HostnameResolver
{
    public static async Task<string?> TryReverseDnsAsync(string ipAddress)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(IPAddress.Parse(ipAddress));

            if (!string.IsNullOrWhiteSpace(entry.HostName))
                return entry.HostName;
        }
        catch
        {
            // Reverse DNS often fails on home networks.
        }

        return null;
    }
}
```

But reverse DNS will often fail unless your router registers DHCP hostnames.

---

## 3. Add mDNS discovery

mDNS is very useful on home networks. It can reveal names like:

```text
homeserver.local
printer.local
chromecast-bedroom.local
iphone.local
```

Add an mDNS provider using a .NET mDNS library such as `Makaretu.Dns` or another maintained mDNS package.

The app should listen for:

```text
.local hostnames
_googlecast._tcp.local
_ssh._tcp.local
_http._tcp.local
_smb._tcp.local
_airplay._tcp.local
_raop._tcp.local
_printer._tcp.local
```

Device table fields to add:

```text
mDNS Name
mDNS Services
Service Port
TXT Records
```

Example result:

```text
IP: 192.168.87.243
Hostname: homeserver.local
Services: _ssh._tcp.local, _http._tcp.local
Ports: 22, 80
```

---

## 4. Add SSDP / UPnP discovery

SSDP can identify routers, TVs, media devices, speakers, IoT bridges and some NAS devices.

You should send an SSDP M-SEARCH to:

```text
239.255.255.250:1900
```

And listen for responses containing:

```text
LOCATION
SERVER
ST
USN
```

Then fetch the `LOCATION` XML and parse:

```text
friendlyName
manufacturer
modelName
modelNumber
serialNumber
deviceType
presentationURL
```

This is one of the easiest ways to populate friendly device names.

Example:

```text
IP: 192.168.0.1
Friendly Name: FAST5366LTE-A
Manufacturer: Sagemcom
Device Type: Internet Gateway Device
```

---

## 5. Add DHCP packet parsing

When a device requests an IP, DHCP often includes the hostname in option 12.

Capture:

```text
UDP 67
UDP 68
```

Extract:

```text
Client MAC
Requested IP
Assigned IP
Hostname
Vendor Class Identifier
Parameter Request List
```

Useful fields:

```text
DHCP Hostname
DHCP Vendor Class
DHCP Server
Lease Time
```

This is very good for identifying phones, laptops and Linux servers.

The limitation: you only see this when the device renews DHCP or reconnects.

---

## 6. Improve the device model

Right now your table is:

```text
MAC address
IP addresses
Hostname
Vendor
First seen
Last seen
```

I would expand it to this:

| Field         | Why it matters                              |
| ------------- | ------------------------------------------- |
| Display Name  | Best resolved name or manual alias          |
| IP Addresses  | Current and previous IPs                    |
| MAC Address   | Physical/private identifier                 |
| Vendor        | OUI lookup                                  |
| Hostname      | DNS/mDNS/DHCP/NBNS                          |
| Device Type   | Router, phone, laptop, server, printer, IoT |
| Segment       | `192.168.0.0/24`, `192.168.87.0/24`         |
| Seen Via      | ARP, IP packet, mDNS, SSDP, DHCP, Nmap      |
| Same Subnet   | Whether directly reachable by ARP           |
| Gateway/Route | How your PC reaches it                      |
| Open Ports    | SSH, HTTP, HTTPS, SMB, RDP, etc.            |
| Services      | `_ssh`, `_http`, `_googlecast`, SMB, etc.   |
| Last Traffic  | Last observed packet/flow                   |
| Confidence    | How sure you are about the identity         |
| Notes/Alias   | User-defined name                           |

The app should store multiple observations per device, not overwrite everything into one row.

---

## 7. How to identify which router/switch a device is connected to

This is the hardest part.

With your hardware:

```text
Eero
FAST5366LTE-A
Google Nest mesh
TP-Link 5-port desktop switch
```

you probably cannot get exact switch-port-level topology unless the switch/router exposes it. A normal 5-port TP-Link desktop switch is usually unmanaged, so it will not tell you which device is on which port.

You can still infer a lot.

### What you can identify confidently

| Detection                         | Meaning                                    |
| --------------------------------- | ------------------------------------------ |
| Device IP is `192.168.0.xxx`      | Likely on Optus/FAST5366LTE-A side         |
| Device IP is `192.168.87.xxx`     | Likely on Google Nest side                 |
| Default gateway is `192.168.0.1`  | Optus-side routing                         |
| Default gateway is `192.168.87.1` | Google Nest-side routing                   |
| mDNS/SSDP source interface        | Which local interface saw the announcement |
| Traceroute first hop              | Which gateway routes to the device         |
| Router DHCP lease table           | Best source if accessible                  |
| Wi-Fi BSSID                       | Which AP your own PC is connected to       |

### What you cannot know reliably without better equipment

```text
Exact TP-Link switch port
Exact Google mesh node for another client
Exact Eero node association
All traffic between two other devices
Whether an unmanaged switch has a hidden device
```

For exact topology, you need one of these:

```text
Managed switch with port mirroring
Switch with SNMP/LLDP
Router APIs
A sensor plugged into each subnet
A Linux box acting as gateway/bridge
```

---

## 8. Add route-aware classification

For each device, add a “Reachability Type”:

```text
Local Layer-2
Routed
NATed
Unknown
Unreachable
```

Logic:

```text
If IP is in same subnet as local interface:
    classify as Local Layer-2
    ARP should work

If IP is not in same subnet but TCP/ping works:
    classify as Routed

If IP is behind another subnet and only reachable through port-forwarding:
    classify as NATed

If no ping and no TCP:
    classify as Unreachable
```

For your current example:

```text
PC:      192.168.0.75
Server:  192.168.87.243

Classification:
Routed, not local ARP
```

Add a route check using:

```powershell
Find-NetRoute -RemoteIPAddress 192.168.87.243
```

or in C# by shelling out initially.

---

## 9. Add open port scanning

For your own LAN, add a controlled TCP connect scan.

Common ports:

```text
22    SSH
23    Telnet
53    DNS
80    HTTP
443   HTTPS
445   SMB
548   AFP
631   IPP printer
3389  RDP
5000  Synology / apps
8000  Dev servers
8080  HTTP alternative
8443  HTTPS alternative
9100  Printer RAW
32400 Plex
```

### C# TCP port scanner

```csharp
public static class PortScanner
{
    public static async Task<IReadOnlyList<int>> ScanOpenPortsAsync(
        IPAddress ip,
        IEnumerable<int> ports,
        TimeSpan timeout)
    {
        var tasks = ports.Select(port => IsOpenAsync(ip, port, timeout));
        var results = await Task.WhenAll(tasks);

        return results
            .Where(r => r.IsOpen)
            .Select(r => r.Port)
            .OrderBy(p => p)
            .ToList();
    }

    private static async Task<(int Port, bool IsOpen)> IsOpenAsync(
        IPAddress ip,
        int port,
        TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();

            var connectTask = client.ConnectAsync(ip, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout));

            if (completed != connectTask)
                return (port, false);

            return (port, client.Connected);
        }
        catch
        {
            return (port, false);
        }
    }
}
```

Usage:

```csharp
var commonPorts = new[]
{
    22, 23, 53, 80, 443, 445, 548, 631, 3389,
    5000, 8000, 8080, 8443, 9100, 32400
};

var openPorts = await PortScanner.ScanOpenPortsAsync(
    IPAddress.Parse("192.168.87.243"),
    commonPorts,
    TimeSpan.FromMilliseconds(700)
);
```

Then show:

```text
192.168.87.243
Open ports: 22 SSH, 80 HTTP, 443 HTTPS
Suggested action: SSH available
```

---

## 10. Add service detection

After finding open ports, lightly identify services.

For example:

### SSH

Connect to port 22 and read the banner:

```text
SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13
```

### HTTP/HTTPS

Request:

```text
GET / HTTP/1.1
Host: <ip>
```

Extract:

```text
Status code
Server header
Title tag
Redirect location
```

Then your app can show:

```text
Port 80: nginx - Home Assistant
Port 22: OpenSSH
Port 445: SMB
```

This is more useful than only showing “port open”.

---

## 11. Add traffic visibility

For each device, track flows:

```text
Source IP
Destination IP
Source port
Destination port
Protocol
Bytes
Packets
First seen
Last seen
Direction: local/local, local/internet, internet/local
```

Show:

```text
Device: 192.168.87.243
Talking to:
  192.168.0.75: SSH
  1.1.1.1: DNS/HTTPS
  github.com: HTTPS
```

You can get domain names from:

```text
DNS queries
mDNS
TLS SNI where visible
HTTP Host header where unencrypted
Reverse DNS
```

But modern traffic is mostly encrypted, so do not expect payload visibility.

---

## 12. Can you detect sniffers on the network?

Partially, but not perfectly.

A passive sniffer is designed to be quiet. On a switched network, a device simply listening may be invisible.

What you can detect are **network attacks or suspicious conditions**, not all sniffers.

Add checks for:

| Check                   | What it detects                   |
| ----------------------- | --------------------------------- |
| Gateway MAC change      | Possible ARP spoofing / MITM      |
| Duplicate IP            | Misconfigured or malicious device |
| Duplicate MAC           | VM/bridge issue or spoofing       |
| Rogue DHCP server       | Major network risk                |
| Unexpected DNS server   | Hijacking/misconfig               |
| New unknown device      | Device joined network             |
| Open Telnet/FTP         | Weak services                     |
| Promiscuous mode probes | Unreliable, optional              |
| ARP storm               | Misbehaving device                |
| SSDP/UPnP exposure      | Risky router/device config        |

Useful alerts:

```text
Gateway 192.168.0.1 MAC changed from X to Y
New DHCP server detected at 192.168.0.x
Device started advertising Telnet
Unknown device joined Google Nest subnet
Server moved from 192.168.0.148 to 192.168.87.243
```

---

## 13. Suggested tabs for the app

### Dashboard

```text
Current interface
Current subnet
Default gateway
Devices online
Unknown devices
Routers detected
Servers reachable
Warnings
```

### Devices

Your current table, expanded.

### Topology

Inferred map:

```text
Internet / Origin NBN
        |
      Eero
        |
 FAST5366LTE-A / 192.168.0.0/24
        |
   -----------------------------
   |                           |
TP-Link Switch              Google Nest / 192.168.87.0/24
   |                           |
Servers                    Mesh devices / Wi-Fi clients
```

Each link should show confidence:

```text
Certain
Likely
Inferred
Unknown
```

### Server Locator

This is important for your SSH problem.

```text
Home Server
Known IPs:
  192.168.0.148   SSH closed
  192.168.87.243  SSH open

Recommended:
  ssh gagneet@192.168.87.243
```

### Ports & Services

```text
Device
Open ports
Service
Banner
Risk
```

### Traffic

```text
Top talkers
DNS requests
Local flows
Internet flows
Broadcast/multicast traffic
```

### Alerts

```text
New device
Gateway changed
Server moved subnet
Rogue DHCP
Open port change
```

---

## 14. Priority build order

Do this next:

1. **Fix IP-to-MAC mapping rules**

   * Only trust ARP/DHCP for IP→MAC.
   * Do not blindly bind remote IPs to Ethernet MACs from routed packets.

2. **Add OUI vendor lookup**

   * Simple CSV-based lookup.

3. **Add reverse DNS**

   * Easy and quick.

4. **Add mDNS discovery**

   * Big improvement for names.

5. **Add SSDP discovery**

   * Big improvement for routers, TVs, IoT, media devices.

6. **Add TCP port scan**

   * Start with common ports only.

7. **Add route-aware reachability**

   * Show whether a device is local, routed, or unreachable.

8. **Add server locator**

   * Your most useful feature.

9. **Add service detection**

   * SSH banner, HTTP title, SMB presence.

10. **Add alerts**

* New device, changed IP, changed gateway, changed open ports.

---

## 15. Suggested device identity scoring

When merging observations, score like this:

```text
MAC from ARP:                 strong
MAC from DHCP client field:   strong
Hostname from DHCP:           strong
Hostname from mDNS:           strong
Hostname from reverse DNS:    medium
SSDP friendly name:           medium/high
Vendor from OUI:              medium
IP packet only:               weak
```

Example:

```text
Device: Home Server
Confidence: 92%

Evidence:
  SSH open on 192.168.87.243
  mDNS name homeserver.local
  MAC seen via ARP/DHCP
  Vendor: Intel
  Previously known as 192.168.0.148
```

---

## 16. The feature you should add for your own problem

Add this panel:

```text
Known Critical Devices
```

Example:

```text
Home Server
Status: Online
Current IP: 192.168.87.243
Previous IP: 192.168.0.148
SSH: Open
Route: Routed via 192.168.0.1
Connect:
ssh gagneet@192.168.87.243
Warning:
Server is currently on Google Nest subnet while this PC is on Optus subnet.
```

This will directly solve the issue you described.

