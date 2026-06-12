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

## Runtime Notes

Packet capture normally requires a native capture driver:

- Windows: install Npcap.
- Linux: install libpcap and run with capture privileges.
- macOS: use libpcap-compatible capture permissions.

The first capture filter is `ip or arp`. ARP visibility is limited to the local layer-2 segment; routed networks still need active checks such as ping, TCP connect, route inspection, and traceroute in later discovery modules.
