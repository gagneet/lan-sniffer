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

| Project | Description |
|---|---|
| `LanInspector.Core` | Platform-neutral models, analyzers, SSH command generation, Tailscale integration, route diagnosis, known-device config. |
| `LanInspector.Platform.Windows` | Windows route diagnostics (`Find-NetRoute`, `tracert`), Windows Terminal / PowerShell launcher, Npcap detection. |
| `LanInspector.Platform.Linux` | Linux route diagnostics (`ip route`, `tracepath`, `traceroute`), terminal launcher, libpcap / capability checks. |
| `LanInspector.Platform.MacOS` | macOS route diagnostics (`route -n get`, `traceroute`), BPF capture checks. |
| `LanInspector.Cli` | Cross-platform CLI executable (`laninspector`). |
| `LanInspector.UI` | Windows WPF desktop application. |
| `LanInspector.Tests` | Unit tests for Core logic. |

## CLI Usage

```bash
laninspector status                     # Network summary and Tailscale state
laninspector interfaces                 # List active network interfaces
laninspector known                      # List known devices from config
laninspector check home-server          # Check reachability of a known device
laninspector check-ip 192.168.87.243 --port 22
laninspector route 192.168.87.243       # Route to IP
laninspector trace 192.168.87.243       # Traceroute with route diagnosis
laninspector ssh home-server --print    # Print SSH command
laninspector ssh home-server --open     # Open SSH in terminal
laninspector tailscale status           # Tailscale peer list
laninspector tailscale routes           # Subnet route command suggestions
laninspector recommend home-server      # Connection recommendations
laninspector capture-prereqs            # Check packet capture prerequisites
```

Route diagnostics, Tailscale, and SSH command generation all work without packet capture privileges.

## Remote Access Manager

The `recommend` command analyses your known devices and the current network to suggest the best connection method:

1. **Direct LAN IP** — if SSH port is reachable from the current machine.
2. **Tailscale IP / hostname** — if the device is found in your Tailscale tailnet.
3. **Subnet route guidance** — if the route exits via CGNAT (`100.64.0.0/10`), the app warns you and shows the `tailscale up --advertise-routes` command to run on a Linux server.

## Tailscale Integration

Tailscale is detected via `tailscale status --json`. The CLI shows:

- `Not installed` / `Installed but not connected` / `Connected`
- Peer list with online/offline status
- Local Tailscale IP
- Which known devices are visible in the tailnet

No Tailscale API key is required. The integration reads local CLI output only.

## SSH Actions

Core generates SSH commands. Platform launchers open a terminal:

- **Windows**: Windows Terminal (`wt.exe`) or PowerShell (`powershell.exe -NoExit`)
- **Linux**: `x-terminal-emulator`, `gnome-terminal`, `konsole`, or `xterm`
- **macOS**: `open -a Terminal` (command printed to stdout)

No SSH passwords are stored. Authentication uses your local SSH keys, `ssh-agent`, or Windows OpenSSH.

## Known Device Config

Create `known-devices.json` in the current directory, `~/.config/laninspector/`, or the executable directory:

```json
{
  "knownDevices": [
    {
      "id": "home-server",
      "displayName": "Home Server",
      "deviceType": "Server",
      "knownIps": ["192.168.0.148", "192.168.87.243"],
      "knownTailscaleNames": ["home-server", "homeserver"],
      "ssh": {
        "enabled": true,
        "user": "your-username",
        "port": 22
      },
      "tags": ["critical", "server"]
    }
  ]
}
```

Override defaults without modifying the shipped file by creating `known-devices.local.json` next to `known-devices.json`.

## Capture Prerequisites

Packet capture requires a native driver. Route and SSH features work without it.

| Platform | Requirement |
|---|---|
| Windows | Install [Npcap](https://npcap.com/) |
| Linux | `sudo apt-get install libpcap-dev` and either run as root or `sudo setcap cap_net_raw,cap_net_admin=eip ./laninspector` |
| macOS | Run with `sudo` or adjust BPF device permissions |

Check prerequisites with: `laninspector capture-prereqs`

## Building

```bash
dotnet build LanInspector.sln -c Release
dotnet test tests/LanInspector.Tests/LanInspector.Tests.csproj
```

The WPF project (`LanInspector.UI`) requires Windows or the `EnableWindowsTargeting` build property. The CLI and Core build on any platform.

## Publishing

### Windows WPF

```powershell
.\scripts\publish-windows.ps1
```

Output: `artifacts\LanInspector-win-x64` (Npcap must be installed separately on the target machine.)

### CLI (all targets)

```powershell
.\scripts\publish-cli.ps1
```

or on Linux/macOS:

```bash
./scripts/publish-cli.sh
```

Targets: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. Artifacts written to `artifacts/`.

## Security and Privacy

- No router admin passwords are stored.
- No SSH passwords are stored.
- Authentication uses normal OpenSSH, SSH keys, `ssh-agent`, or Windows OpenSSH.
- Tailscale is the recommended secure remote access layer for cross-network connectivity.
- Router configuration guidance is shown as manual instructions only — the app never logs into routers automatically.
- Use only on networks you own or are authorised to inspect.
- Active scans are user-triggered.
- PCAP/PCAPNG files can contain sensitive metadata and payloads. Treat them as private.

## NuGet Packages

Current packages:

- `SharpPcap` `6.3.1` — packet capture
- `PacketDotNet` `1.4.8` — packet parsing
- `CommunityToolkit.Mvvm` `8.4.2` — WPF MVVM
- `xunit` `2.9.3` — unit tests

Planned next-phase packages:

- `LiveChartsCore` or `OxyPlot` — traffic charts
- `Lextm.SharpSnmpLib` — SNMP support

## Cross-Platform Status

| Feature | Windows | Linux | macOS |
|---|---|---|---|
| Route diagnostics | `Find-NetRoute` + `tracert` | `ip route` + `tracepath` | `route -n get` + `traceroute` |
| Tailscale status | `tailscale.exe` | `tailscale` | `tailscale` |
| SSH command generation | Yes | Yes | Yes |
| Terminal launcher | Windows Terminal / PowerShell | gnome-terminal / konsole / xterm | Terminal.app |
| Packet capture | Npcap | libpcap + cap_net_raw | libpcap / BPF |
| WPF UI | Yes | No (future Avalonia phase) | No (future Avalonia phase) |

## Current Capabilities

- Passive ARP device discovery for trusted same-subnet IP-to-MAC mapping.
- DNS and mDNS packet parsing for observed names.
- DHCP packet parsing for client hostname, vendor class, requested IP and DHCP server hints.
- CSV-based OUI vendor lookup.
- Reverse DNS fallback for devices that have an IP but no captured hostname yet.
- Opt-in common TCP port scan from the selected device row.
- Route-aware device classification with local segment, gateway and route summary fields.
- Known critical devices with SSH command actions for quick connection checks.
- Cross-platform platform abstraction for Windows, Linux and macOS service implementations.
- CLI for status, interfaces, known devices, route checks, trace, SSH command generation, Tailscale status, remote access recommendations, and capture prerequisite checks.
- Tailscale parsing and recommendation engine for remote access and subnet-router guidance.
- RFC1918 / CGNAT route misconfiguration detection (e.g. Eero routing 192.168.87.x upstream via 100.64.x.x).

## Next Phase Roadmap

The next major phase is documented in:

- [`docs/next-phase-topology-traffic-dns-integrations.md`](docs/next-phase-topology-traffic-dns-integrations.md)
- [`docs/next-phase-cross-platform-cli-remote-access.md`](docs/next-phase-cross-platform-cli-remote-access.md)

Planned next-phase capabilities:

- Avalonia cross-platform GUI (Linux / macOS desktop).
- Topology snapshot with confidence and evidence.
- LAN/NAT visibility explanation engine.
- Traffic-flow aggregation and charts.
- Optional Nmap integration for active discovery.
- Optional Wireshark/TShark integration for capture export and deep packet summaries.
- Optional Pi-hole/AdGuard Home integration for DNS visibility.
- SNMP and passive LLDP topology evidence foundation.

## Example Network Scenario

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

LanInspector explains this in plain English: the Eero route does not know how to reach the Google Nest subnet, while the Optus route does.

## Documentation

- [User Guide](docs/user-guide.md)
- [Next Phase: Topology, Traffic, DNS and Integrations](docs/next-phase-topology-traffic-dns-integrations.md)
- [Cross-platform CLI and Remote Access Prompt](docs/next-phase-cross-platform-cli-remote-access.md)
