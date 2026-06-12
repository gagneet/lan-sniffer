# LanInspector

LanInspector is a .NET 8 WPF desktop application for local network inspection. The initial framework is intentionally generic: it enumerates capture interfaces, starts packet capture with a configurable BPF filter, analyzes ARP packets, and surfaces observed devices in the UI.

## Projects

- `LanInspector.Core`: packet capture, network models, discovery, analyzers, and plugin contracts.
- `LanInspector.UI`: WPF shell and MVVM view models.

## Project Settings

- Core target framework: `net8.0`
- UI target framework: `net8.0-windows`
- WPF: enabled with `<UseWPF>true</UseWPF>`
- Cross-OS restore/build support: `<EnableWindowsTargeting>true</EnableWindowsTargeting>`
- Nullable reference types: enabled in `Directory.Build.props`
- Implicit usings: enabled in `Directory.Build.props`

## NuGet Packages

- `SharpPcap` `6.3.1`: packet capture provider.
- `PacketDotNet` `1.4.8`: packet parsing and protocol extraction.
- `CommunityToolkit.Mvvm` `8.4.2`: ViewModel base classes and relay commands.

## Current Capabilities

- Passive ARP device discovery for trusted same-subnet IP-to-MAC mapping.
- DNS and mDNS packet parsing for observed hostnames.
- DHCP packet parsing for client hostname, vendor class, requested IP, and DHCP server hints.
- CSV-based OUI vendor lookup from `src/LanInspector.UI/Data/oui.csv`.
- Reverse DNS fallback for devices that have an IP but no captured hostname yet.
- Opt-in common TCP port scan from the selected device row.
- Route-aware device classification with local segment, gateway and route summary fields.
- Seeded known critical devices with SSH command actions for quick connection checks.

## Runtime Notes

Packet capture normally requires a native capture driver:

- Windows: install Npcap.
- Linux: install libpcap and run with capture privileges.
- macOS: use libpcap-compatible capture permissions.

The default capture filter is `ip or arp or udp port 53 or udp port 5353 or udp port 67 or udp port 68`. ARP visibility is limited to the local layer-2 segment; routed networks still need active checks such as ping, TCP connect, route inspection, and traceroute in later discovery modules.
