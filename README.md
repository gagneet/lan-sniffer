# LanInspector

LanInspector is a .NET-based local network inspection, route diagnosis, remote access and traffic visibility tool for user-owned networks.

It helps answer practical home-network questions such as:

- Which network/subnet am I connected to?
- Which devices are visible from this machine?
- Which devices are local, routed, behind NAT, or unreachable?
- Why can I SSH from one Wi-Fi network but not another?
- Which devices expose SSH, HTTP, SMB, RDP or other common ports?
- What traffic can this machine actually see?
- What extra evidence can be added through Tailscale, Nmap, DNS providers, Wireshark/TShark, SNMP, LLDP, or a managed switch?

## Projects

- `LanInspector.Core`: packet capture, network models, discovery, analyzers, topology models, remote-access logic and plugin contracts.
- `LanInspector.Platform.Windows`: Windows route diagnostics, terminal launching and capture prerequisite checks.
- `LanInspector.Platform.Linux`: Linux route diagnostics, terminal launching and capture prerequisite checks.
- `LanInspector.Platform.MacOS`: macOS route diagnostics, terminal launching and capture prerequisite checks.
- `LanInspector.Cli`: cross-platform command-line interface.
- `LanInspector.UI`: Windows WPF shell and MVVM view models.

## Project Settings

- Core target framework: `net8.0`
- UI target framework: `net8.0-windows`
- WPF: enabled with `<UseWPF>true</UseWPF>`
- Cross-OS restore/build support: `<EnableWindowsTargeting>true</EnableWindowsTargeting>`
- Nullable reference types: enabled in `Directory.Build.props`
- Implicit usings: enabled in `Directory.Build.props`

## NuGet Packages

Current core packages include:

- `SharpPcap`: packet capture provider.
- `PacketDotNet`: packet parsing and protocol extraction.
- `CommunityToolkit.Mvvm`: ViewModel base classes and relay commands.

Planned/optional next-phase packages may include:

- `LiveChartsCore` or `OxyPlot`: traffic charts.
- `Lextm.SharpSnmpLib`: SNMP support.
- Additional JSON/XML parsing packages only if required.

## Current Capabilities

- Passive ARP device discovery for trusted same-subnet IP-to-MAC mapping.
- DNS and mDNS packet parsing for observed names.
- DHCP packet parsing for client hostname, vendor class, requested IP and DHCP server hints.
- CSV-based OUI vendor lookup.
- Reverse DNS fallback for devices that have an IP but no captured hostname yet.
- Opt-in common TCP port scan from the selected device row.
- Route-aware device classification with local segment, gateway and route summary fields.
- Seeded known critical devices with SSH command actions for quick connection checks.
- Cross-platform platform abstraction work for Windows, Linux and macOS service implementations.
- CLI support for status, interfaces, known devices, route checks, trace checks, SSH command generation, Tailscale status and capture prerequisite checks.
- Tailscale parsing/recommendation groundwork for remote access and subnet-router guidance.

## Next Phase Roadmap

The next major phase is documented in:

- [`docs/next-phase-topology-traffic-dns-integrations.md`](docs/next-phase-topology-traffic-dns-integrations.md)
- [`docs/user-guide.md`](docs/user-guide.md)

Planned next-phase capabilities:

- Topology snapshot with confidence and evidence.
- LAN/NAT visibility explanation engine.
- Traffic-flow aggregation and charts.
- Optional Nmap integration for active discovery.
- Optional Wireshark/TShark integration for capture export and deep packet summaries.
- Optional Pi-hole/AdGuard Home integration for DNS visibility.
- SNMP and passive LLDP topology evidence foundation.
- CLI commands for topology, visibility, traffic, Nmap, DNS, Wireshark/TShark and SNMP/LLDP.

## Runtime Notes

Packet capture normally requires a native capture driver or OS capture permissions.

### Windows

- Install Npcap for packet capture.
- Optional: install Nmap for active scans.
- Optional: install Wireshark/TShark for deep packet analysis.
- Optional: install Tailscale for subnet-independent local/remote SSH.

### Linux

- Install libpcap.
- Run with capture privileges or appropriate capabilities.
- Optional: install Nmap, TShark and Tailscale.

### macOS

- Use libpcap/BPF-compatible capture permissions.
- Optional: install Nmap, Wireshark/TShark and Tailscale.

The default capture filter is:

```text
ip or arp or udp port 53 or udp port 5353 or udp port 67 or udp port 68
```

ARP visibility is limited to the local layer-2 segment. Routed networks still need active checks such as ping, TCP connect, route inspection and traceroute.

## Example Network Scenario

A real-world validation scenario:

```text
Origin NBN / Internet
  -> Eero 6+ router, subnet 192.168.4.0/24
     -> FAST5366LTE-A / Optus modem-router, subnet 192.168.0.0/24
        -> Google Nest router/mesh, subnet 192.168.87.0/24
        -> TP-Link unmanaged 5-port switch
```

Known behaviour:

```text
Client on Eero 192.168.4.x:
  SSH to 192.168.87.243 fails.
  Trace goes to 192.168.4.1 then 100.96.x.x.

Client on Optus 192.168.0.x:
  SSH to 192.168.87.243 works.
  Trace goes to 192.168.0.1 then 192.168.87.243.
```

LanInspector should explain this in plain English: the Eero route does not know how to reach the Google Nest subnet, while the Optus route currently does.

## CLI Quick Start

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
laninspector capture-prereqs
```

Planned next-phase commands:

```bash
laninspector visibility
laninspector topology --mermaid
laninspector traffic top
laninspector nmap ports 192.168.87.243
laninspector wireshark status
laninspector dns summary
laninspector snmp query 192.168.0.1 --community public
```

## Publishing

### Windows UI

```powershell
.\scripts\publish-windows.ps1
```

The resulting self-contained Windows artifact is expected under:

```text
artifacts\LanInspector-win-x64
```

Npcap must still be installed separately on the target Windows machine for packet capture.

### CLI

```powershell
.\scripts\publish-cli.ps1
```

or on Linux/macOS:

```bash
./scripts/publish-cli.sh
```

Expected targets:

```text
win-x64
linux-x64
osx-x64
osx-arm64
```

## Security and Privacy

- Use only on networks you own or are authorised to inspect.
- Active scans are user-triggered.
- Do not run aggressive Nmap/NSE scans by default.
- Do not store router admin passwords.
- Do not store SSH passwords.
- Store DNS API tokens only in local user configuration or OS-secure storage; never commit them to git.
- Packet payload capture/export should require explicit user action.
- PCAP/PCAPNG files can contain sensitive metadata and payloads. Treat them as private.

## Documentation

- [User Guide](docs/user-guide.md)
- [Next Phase: Topology, Traffic, DNS and Integrations](docs/next-phase-topology-traffic-dns-integrations.md)
- [Cross-platform CLI and Remote Access Prompt](docs/next-phase-cross-platform-cli-remote-access.md)
