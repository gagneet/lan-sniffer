<p align="center">
  <img src="assets/laninspector-logo-256.png" alt="LanInspector" width="128" height="128">
</p>

<h1 align="center">LanInspector</h1>

LanInspector is a .NET-based local network inspection, route diagnosis, remote access and traffic visibility tool for user-owned networks.

It helps answer practical home-network questions such as:

- Which network/subnet am I connected to?
- **Which IP is my server on today, now that DHCP has moved it again?**
- Which devices are visible from this machine?
- Which devices are local, routed, behind NAT, or unreachable?
- Why can I SSH from one Wi-Fi network but not another?
- Which devices expose SSH, HTTP, SMB, RDP or other common ports?
- What traffic can this machine actually see, and which host is responsible for it?
- What extra evidence can be added through Tailscale, Nmap, DNS providers, Wireshark/TShark, SNMP, LLDP, or a managed switch?

## Finding a device whose IP keeps changing

```bash
laninspector locate home-server
```

```text
Home Server (ubuntu-svr) (home-server)
  Current LAN IP : 192.168.0.154
  Found via      : ARP cache, matched by MAC
  Confidence     : Confirmed
  Tailscale      : 100.83.183.74  (ubuntu-svr.tail7f7c1e.ts.net)
  Changed        : was 192.168.0.148 at 2026-09-10 21:04:11Z
```

Evidence is gathered from the ARP cache (matched by MAC), Tailscale's live peer endpoints,
`tailscale ping`, DNS/mDNS, the last known address, and the configured address — ranked, then
verified with a TCP probe. The first candidate that answers wins; if none answer, the strongest
unverified candidate is reported and flagged as such.

See [Tracking a Server Whose IP Keeps Changing](docs/tracking-a-moving-server-ip.md) for the full
walkthrough, including DHCP reservations and Tailscale subnet routes.

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
laninspector status                          # Network summary and Tailscale state
laninspector interfaces                      # List active network interfaces
laninspector known                           # List known devices from config
laninspector locate                          # Find the current LAN IP of every known device
laninspector locate home-server              # ...or of one device (alias: whereis)
laninspector locate home-server --probe      # Also run 'tailscale ping' to force a direct path
laninspector locate --json                   # Machine-readable output for scripting
laninspector check home-server               # Check reachability of a known device
laninspector check-ip 192.168.87.243 --port 22
laninspector route 192.168.87.243            # Route to IP
laninspector trace 192.168.87.243            # Traceroute with route diagnosis
laninspector ssh home-server --print         # Print SSH command
laninspector ssh home-server --open          # Open SSH in terminal
laninspector tailscale status                # Tailscale peer list
laninspector tailscale routes                # Subnet route command suggestions
laninspector recommend home-server           # Connection recommendations
laninspector capture-prereqs                 # Check packet capture prerequisites

# Topology and visibility
laninspector topology                        # Network topology snapshot
laninspector topology --mermaid              # Mermaid diagram output
laninspector topology --json                 # JSON output
laninspector visibility                      # Explain visibility to all known devices
laninspector visibility 192.168.87.243       # Explain visibility to a specific IP

# Active scanning (optional, requires nmap)
laninspector nmap status                     # Check if nmap is available
laninspector nmap ping 192.168.87.0/24       # Ping sweep
laninspector nmap ports 192.168.87.243       # TCP port scan (top 100)
laninspector nmap services 192.168.87.243    # Service detection

# Wireshark / TShark (optional)
laninspector tshark status                   # Check if tshark/wireshark are available
laninspector tshark summary capture.pcap     # Show packet summary from file
laninspector tshark open capture.pcap        # Open in Wireshark
laninspector pcap export eth0 30             # Capture 30 seconds from eth0

# DNS filter provider (AdGuard Home or Pi-hole)
laninspector dns status                      # Provider connection and blocking stats
laninspector dns summary                     # Top clients, domains, blocked list
laninspector dns queries 50                  # Recent DNS queries
laninspector dns client 192.168.0.50         # Queries for a specific client

# SNMP
laninspector snmp 192.168.0.1                # Query device via SNMP v2c
laninspector snmp 192.168.0.1 --community private
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
      "knownMacs": ["9c:6b:00:aa:bb:cc"],
      "knownHostnames": ["ubuntu-svr"],
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

| Field | Why it matters |
|---|---|
| `knownMacs` | The MAC does not change when the DHCP lease does, so it is the most reliable way to re-find a device. Accepts `aa:bb:cc:..`, `aa-bb-cc-..` or bare hex. |
| `knownHostnames` | Used for DNS and mDNS (`<name>.local`) lookups when the device is on another segment and its MAC is not in the local ARP cache. |
| `knownTailscaleNames` | Matches the tailnet peer, which supplies both the stable overlay address and live LAN endpoint evidence. |
| `knownIps` | Starting points only — treated as the weakest evidence, because they go stale. |
| `knownSubnets` | For routers known by the range they serve rather than a fixed address. |

Files are **merged by device id, with later files winning**, in this order:

1. `<exe-dir>/Data/known-devices.json`, then `known-devices.local.json`
2. `~/.config/laninspector/` (Linux/macOS) or `%APPDATA%\LanInspector\` (Windows)
3. `./known-devices.json`, then `./known-devices.local.json`

Override the shipped defaults by creating a `known-devices.local.json` containing only the fields you want to change.

## DNS Filter Integration

Create `integrations.json` at:
- **Windows**: `%APPDATA%\LanInspector\integrations.json`
- **Linux/macOS**: `~/.config/laninspector/integrations.json`

AdGuard Home example:
```json
{
  "adGuardHome": {
    "url": "http://192.168.0.1:3000",
    "username": "admin",
    "password": "your-password"
  }
}
```

Pi-hole example:
```json
{
  "piHole": {
    "url": "http://192.168.0.1",
    "apiToken": "your-api-token"
  }
}
```

## Traffic View

The **Traffic** tab aggregates captured packets into throughput over time and per-host totals.

- **Window selector** — last 60 seconds, 15 minutes, 60 minutes or 3 hours. The live view is
  served from one-second buckets; the longer windows from one-minute rollups, so an hour of
  history costs sixty buckets rather than three and a half thousand.
- **Top hosts** — every host ranked by the volume it moved inside the window, split into sent and
  received.
- **Drill-down** — select a host and the chart, the flow list and the peer list all narrow to that
  address. "Show all hosts" returns to the whole-network view.
- Quiet periods are zero-filled rather than compressed away, so the bars line up with wall-clock
  time and a gap in traffic looks like a gap.

Hover any bar for its timestamp, throughput and packet count.

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

- `SharpPcap` `6.3.1` — packet capture
- `PacketDotNet` `1.4.8` — packet parsing
- `CommunityToolkit.Mvvm` `8.4.2` — WPF MVVM
- `Lextm.SharpSnmpLib` `12.5.2` — SNMP v2c discovery
- `xunit` `2.9.3` — unit tests

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
- CLI for status, interfaces, known devices, route checks, trace, SSH command generation, Tailscale status, remote access recommendations, capture prerequisite checks, topology, visibility, nmap, tshark, dns, snmp.
- Tailscale parsing and recommendation engine for remote access and subnet-router guidance.
- RFC1918 / CGNAT route misconfiguration detection (e.g. Eero routing 192.168.87.x upstream via 100.64.x.x).
- **Topology snapshot** with node/edge model, confidence levels (Confirmed/High/Medium/Low/Unknown), evidence tracking, and Mermaid diagram export.
- **Visibility explanation engine** — explains in plain English whether a machine can reach a target IP and why.
- **Device locator** — resolves a known device's current LAN IP from the ARP cache (by MAC), Tailscale peer endpoints, `tailscale ping`, DNS/mDNS, remembered and configured addresses; verifies by TCP probe and records address changes over time.
- **Traffic flow aggregation** — live packets/sec, bytes/sec, per-flow tracking, per-host top-talker ranking, and a throughput chart covering up to three hours with per-host drill-down.
- **Passive LLDP analyzer** — captures EtherType 0x88CC frames and extracts chassis ID, port ID, system name, management address.
- **Nmap integration** — optional ping sweep, TCP connect scan, service detection. Parses XML output.
- **TShark / Wireshark integration** — PCAP export, packet summary, open in Wireshark.
- **AdGuard Home provider** — REST API integration for DNS filter status, top clients, blocked domains, recent queries.
- **Pi-hole provider** — API integration for DNS filter status and query log.
- **SNMP v2c discovery** — queries sysDescr, sysName, interface table, IP address table using SharpSnmpLib.
- **WPF tabs** — Topology, Traffic, and DNS Filter tabs alongside the existing Devices tab.

## Branding

The logo and application icon are generated from a single definition so they never drift apart:

```bash
python3 scripts/generate-logo.py    # requires pillow
```

This writes `assets/laninspector-logo.png` (1024), `assets/laninspector-logo-256.png`,
`src/LanInspector.UI/Assets/laninspector-logo.png` (in-app header) and
`src/LanInspector.UI/Assets/laninspector.ico` (16–256px ladder, used as the window and executable
icon). `assets/laninspector-logo.svg` is the vector master and mirrors the same geometry.

## Next Phase Roadmap

- Avalonia cross-platform GUI (Linux / macOS desktop).
- SNMP FDB table walk for switch port MAC mapping.
- LLDP topology graph overlay (LLDP neighbours to topology nodes).
- Persist traffic history across restarts so the hour view survives a restart of the app.

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
- [Tracking a Server Whose IP Keeps Changing](docs/tracking-a-moving-server-ip.md)
- [Next Phase: Topology, Traffic, DNS and Integrations](docs/next-phase-topology-traffic-dns-integrations.md)
- [Cross-platform CLI and Remote Access Prompt](docs/next-phase-cross-platform-cli-remote-access.md)
