# LanInspector User Guide

LanInspector is a network inspection, route-diagnosis, remote-access and traffic-visibility tool for user-owned networks.

It is intended to help answer practical questions such as:

- Which network am I connected to?
- Which devices are visible from this machine?
- Which devices are on another subnet or behind NAT?
- Why can I SSH from one Wi-Fi network but not another?
- What ports or services are open on a known device?
- What traffic is visible from this machine?
- What can be improved with Tailscale, Nmap, DNS integration, Wireshark, SNMP, LLDP, or a managed switch?

---

## 1. Core Concepts

### Local Layer-2 visibility

ARP and most passive discovery only work on the local layer-2 network segment. If your machine is on `192.168.4.0/24`, it will not normally ARP-discover devices on `192.168.87.0/24`.

### Routed visibility

A device may still be reachable even if it is not ARP-visible. For example, a Windows client on `192.168.0.75` may reach a server on `192.168.87.243` if the route through the Optus/FAST5366LTE-A network works.

### NAT and nested routers

If you have multiple routers, each may create its own subnet and NAT boundary. Example:

```text
Eero:          192.168.4.0/24
Optus router:  192.168.0.0/24
Google Nest:   192.168.87.0/24
```

LanInspector should explain which subnet you are on, what route is used to reach a device, and why a target is reachable or unreachable.

### Passive vs active discovery

Passive discovery listens to traffic. It is safe but incomplete. Quiet devices may not appear.

Active discovery tests devices or subnets. It is more complete but should be user-triggered. Examples include ping, common TCP port scan, Nmap scans and SNMP queries.

---

## 2. Installation Requirements

### Windows

Required for packet capture:

```text
Npcap
```

Optional tools:

```text
Nmap
Wireshark / TShark
Tailscale
Windows Terminal
```

### Ubuntu/Linux

Required for packet capture:

```bash
sudo apt install libpcap0.8
```

Depending on distribution and capture mode, you may need sudo or capabilities.

Optional tools:

```bash
sudo apt install nmap tshark
```

Tailscale can be installed separately from the official Tailscale package instructions.

### macOS

Packet capture uses libpcap/BPF permissions. Optional tools can be installed via Homebrew:

```bash
brew install nmap wireshark tailscale
```

---

## 3. Running the Windows UI

From source:

```powershell
dotnet run --project src/LanInspector.UI/LanInspector.UI.csproj
```

From published output:

```powershell
.\LanInspector.UI.exe
```

The Windows UI is the richer interface. It is currently the primary desktop UI.

---

## 4. Running the CLI

The CLI is intended to work on Windows, Linux and macOS.

Typical commands:

```bash
laninspector status
laninspector interfaces
laninspector known
laninspector check home-server
laninspector check-ip 192.168.87.243 --port 22
laninspector route 192.168.87.243
laninspector trace 192.168.87.243
laninspector ssh home-server --print
laninspector visibility
laninspector topology --mermaid
```

Future next-phase commands:

```bash
laninspector traffic top
laninspector traffic flows
laninspector nmap status
laninspector nmap ports 192.168.87.243
laninspector wireshark status
laninspector dns summary
laninspector snmp query 192.168.0.1 --community public
```

---

## 5. Dashboard

The dashboard should show:

- current interface;
- current IP/subnet;
- default gateway;
- capture status;
- critical devices;
- route warnings;
- Tailscale/remote access state;
- traffic summary;
- topology warnings.

Example warning:

```text
You are connected to Eero 192.168.4.0/24.
The target 192.168.87.243 is behind Google Nest.
Your route exits via 100.96.x.x, so Eero does not know how to reach the internal Google Nest subnet.
```

---

## 6. Devices

The device table should show:

- display name;
- IP addresses;
- MAC address;
- vendor;
- hostname/source;
- DHCP class;
- seen-via evidence;
- reachability;
- route summary;
- open ports;
- tags;
- last seen.

Use manual aliases for important devices when auto-detection is incomplete.

---

## 7. Known Critical Devices

Known devices are configured locally and should include items such as:

```text
Home Server
FAST5366LTE-A / Optus Router
Google Nest
Eero
NAS
Printer
```

For the Home Server example:

```text
Known IPs:
  192.168.0.148
  192.168.87.243
SSH user:
  gagneet
SSH port:
  22
```

LanInspector should test configured connection candidates and recommend the best current SSH command.

---

## 8. SSH Actions

For a device with SSH enabled, LanInspector should show:

```bash
ssh gagneet@192.168.87.243
```

or, for a non-standard port:

```bash
ssh gagneet@192.168.0.5 -p 2222
```

Actions:

- copy SSH command;
- open in Windows Terminal;
- open in PowerShell;
- print command from CLI.

LanInspector must not store SSH passwords.

---

## 9. Remote Access and Tailscale

Tailscale is the recommended way to reach the server from outside the LAN or from a subnet that cannot route to the server directly.

Useful checks:

```bash
tailscale status
tailscale ip -4
```

LanInspector should parse Tailscale status where available and show whether a known server is online through Tailscale.

For a Linux server that should expose its local subnet through Tailscale, the setup assistant may generate:

```bash
sudo tailscale up --advertise-routes=192.168.87.0/24
```

Then approve the route in the Tailscale admin console.

---

## 10. Topology

The topology view should show nodes and edges with confidence and evidence.

Example:

```text
Internet / Origin NBN
  -> Eero / 192.168.4.0/24
     -> FAST5366LTE-A / 192.168.0.0/24
        -> Google Nest / 192.168.87.0/24
           -> Home Server / 192.168.87.243
```

Confidence levels:

```text
Confirmed: explicit interface, gateway, SNMP/LLDP, or known config evidence.
High: ARP/DHCP/route evidence agrees.
Medium: subnet and route evidence suggest relationship.
Low: weak inference from vendor/name/service.
Unknown: insufficient evidence.
```

CLI:

```bash
laninspector topology
laninspector topology --json
laninspector topology --mermaid
```

---

## 11. Traffic Flows

Traffic flows aggregate packets into conversations:

```text
source IP:port -> destination IP:port protocol
```

The app should show:

- top talkers;
- top services;
- local-to-local traffic;
- local-to-internet traffic;
- multicast/broadcast traffic;
- per-device flows;
- live bytes/packets over time.

LanInspector should not store packet payloads by default.

---

## 12. Nmap Integration

Nmap is optional and user-triggered.

Useful modes:

```bash
laninspector nmap status
laninspector nmap ping 192.168.0.0/24
laninspector nmap ports 192.168.87.243
laninspector nmap services 192.168.87.243
```

Safe defaults:

- host discovery;
- common TCP ports;
- light service detection.

Avoid aggressive scans by default.

Only scan networks you own or are authorised to test.

---

## 13. Wireshark and TShark Integration

Wireshark/TShark should be optional.

Use cases:

- export current capture to PCAP/PCAPNG;
- open saved capture in Wireshark;
- use TShark to generate deeper protocol summaries;
- inspect DNS/mDNS/SSDP/DHCP traffic beyond what LanInspector parses natively.

Add privacy warning before exporting captures. PCAP files may contain sensitive metadata or payloads.

---

## 14. DNS Provider Integration

Pi-hole and AdGuard Home can add network-wide DNS visibility.

LanInspector should use a provider abstraction and show:

- provider status;
- top clients;
- top domains;
- blocked queries;
- recent queries;
- queries by selected device.

API tokens should be stored locally and excluded from git.

---

## 15. SNMP and LLDP

SNMP and LLDP can provide better topology evidence when supported by routers, switches or access points.

Consumer routers and unmanaged switches may not support these.

If unavailable, LanInspector should say:

```text
Exact switch-port topology is unavailable because this router/switch does not expose SNMP/LLDP and the TP-Link switch appears unmanaged.
Use a managed switch with SNMP/LLDP or port mirroring for better topology visibility.
```

---

## 16. Understanding What LanInspector Can and Cannot See

### It can usually see

- local ARP devices on the current subnet;
- DNS/mDNS/DHCP packets visible to the selected interface;
- traffic to/from the current machine;
- some broadcast/multicast traffic;
- routed reachability through ping/TCP/traceroute;
- Tailscale peers if Tailscale is installed;
- DNS activity if Pi-hole/AdGuard is integrated.

### It usually cannot see from one Wi-Fi client alone

- all traffic between two other wired devices;
- all Google Nest 2.4GHz IoT clients;
- exact mesh node association;
- exact switch port for devices on an unmanaged switch;
- traffic behind NAT unless routed through the current interface or captured by another sensor.

### How to improve visibility

- run a LanInspector sensor on each subnet;
- use Tailscale for remote access;
- use Pi-hole/AdGuard for DNS visibility;
- use a managed switch with port mirroring;
- use SNMP/LLDP-capable switches/APs;
- use Wireshark/TShark for deep capture analysis.

---

## 17. Troubleshooting

### Server reachable from Optus but not Eero

Symptoms:

```text
From Eero 192.168.4.x:
  Test-NetConnection 192.168.87.243 -Port 22 fails
  tracert goes to 192.168.4.1 then 100.96.x.x

From Optus 192.168.0.x:
  Test-NetConnection succeeds
  tracert goes to 192.168.0.1 then 192.168.87.243
```

Meaning:

```text
Eero does not have a route to the Google Nest subnet.
Optus has a working route/path to the server.
```

Recommended fixes:

1. Connect to Optus Wi-Fi and SSH directly.
2. Use Tailscale for subnet-independent SSH.
3. Collapse the network into fewer subnets if practical.
4. Add static routes only if supported and understood.

### No devices appear

Check:

- capture driver installed;
- correct interface selected;
- capture permissions;
- BPF filter not too narrow;
- devices are active or broadcasting.

### Vendor missing

Check:

- OUI database loaded;
- MAC is not randomized/private;
- MAC was learned from ARP/DHCP rather than a routed packet.

### Hostname missing

Try:

- wait for DHCP renewal;
- check mDNS traffic;
- run reverse DNS;
- use DNS provider integration;
- add manual alias.
