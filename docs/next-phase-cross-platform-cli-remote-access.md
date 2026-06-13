# Next Phase Prompt: Cross-Platform CLI, Remote Access Manager, and Windows Packaging

## Recommendation

Add CLI and cross-platform architecture **now**, but do not migrate the WPF UI to Avalonia yet.

The next phase should keep the Windows WPF application as the primary UI while extracting the reusable logic into platform-neutral services and adding a CLI that can run on Windows, Ubuntu, and macOS. This gives the project a clean foundation for later Ubuntu/macOS UI support without slowing down the immediate Windows use case.

## Why this belongs in the current next-phase prompt

The CLI should be included in the current prompt because it forces the right architecture:

- `LanInspector.Core` remains platform-neutral.
- Windows-only code stays outside Core.
- Route diagnostics, terminal launch, packet capture, and publishing become platform-specific services.
- The same remote-access and SSH logic can be used from both the WPF UI and CLI.
- Ubuntu/macOS support can begin with CLI before attempting a full desktop UI.

The full Avalonia UI migration should be deferred to a later phase.

## Target architecture

```text
LanInspector.Core
  Cross-platform models, analyzers, device identity, connection recommendations,
  known-device config, Tailscale parsing, SSH command generation.

LanInspector.Platform
  Platform abstractions and shared contracts.

LanInspector.Platform.Windows
  Windows route diagnostics, Windows Terminal/PowerShell launcher,
  Npcap availability detection.

LanInspector.Platform.Linux
  Linux route diagnostics using ip/tracepath/traceroute,
  shell SSH launcher, libpcap/tcpdump capability checks.

LanInspector.Platform.MacOS
  macOS route diagnostics using route/netstat/traceroute,
  Terminal/iTerm launcher where possible, BPF/libpcap checks.

LanInspector.Cli
  Cross-platform command-line tool for status, devices, route checks,
  SSH recommendations, Tailscale status, and capture diagnostics.

LanInspector.UI
  Existing Windows WPF app. It consumes Core and Windows platform services.

LanInspector.Avalonia
  Deferred future project for cross-platform GUI.
```

## Development prompt

You are working in the GitHub repository:

```text
https://github.com/gagneet/lan-sniffer
```

Application context:

LanInspector is currently a .NET 8 WPF Windows desktop app for LAN inspection. It has:

- `LanInspector.Core` for packet capture, network models, discovery, analyzers, and plugin contracts.
- `LanInspector.UI` for the WPF shell and MVVM view models.
- Passive ARP discovery.
- DNS/mDNS parsing.
- DHCP parsing.
- OUI lookup.
- Reverse DNS fallback.
- Opt-in common TCP port scanning.
- Route-aware classification.
- Known critical devices.
- SSH command actions.

The user network context is:

```text
Eero 6+ network:          192.168.4.0/24
Optus FAST5366LTE-A:      192.168.0.0/24
Google Nest network:      192.168.87.0/24
Known server current IP:  192.168.87.243
Known server old IP:      192.168.0.148
```

Observed behaviour:

- From Optus `192.168.0.75`, SSH to `192.168.87.243:22` works.
- From Eero `192.168.4.32`, SSH to `192.168.87.243:22` fails.
- Eero route to `192.168.87.243` goes via `192.168.4.1` then `100.96.x.x`, which indicates that Eero is sending the packet upstream instead of routing internally to the Google Nest subnet.

Goal:

Implement the next phase:

```text
Remote Access Manager + SSH connection orchestration + cross-platform CLI + Windows publish packaging.
```

The application should help the user understand and establish access to known devices across a multi-router LAN and from outside the LAN.

## Security rules

Do not store router admin usernames or passwords.
Do not store SSH passwords.
Do not attempt to automate consumer router login as the primary solution.
Use normal OpenSSH, SSH keys, ssh-agent, Windows OpenSSH, or the user's terminal authentication.
Treat Tailscale as the recommended secure remote access layer.
Router configuration should be shown as manual guidance only.

## Functional requirements

### 1. Add platform boundaries

Create or refine the platform abstraction layer.

Add interfaces such as:

```csharp
public interface IRouteDiagnosticsService
{
    Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken ct);
    Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken ct);
    Task<PortReachability> TestTcpPortAsync(IPAddress target, int port, CancellationToken ct);
}

public interface ITerminalLauncher
{
    Task LaunchSshAsync(SshCommand command, CancellationToken ct);
    Task CopyCommandAsync(string command, CancellationToken ct);
}

public interface ITailscaleService
{
    Task<TailscaleStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<IPAddress>> GetLocalTailscaleIpsAsync(CancellationToken ct);
}

public interface ICapturePrerequisiteService
{
    Task<CapturePrerequisiteStatus> CheckAsync(CancellationToken ct);
}
```

Core must not directly call `powershell.exe`, `wt.exe`, `ip`, `route`, `traceroute`, or OS-specific APIs. Core should depend on interfaces.

### 2. Add cross-platform route diagnostics

Implement route diagnostics for:

#### Windows

Use:

```text
Find-NetRoute -RemoteIPAddress <ip>
tracert -d <ip>
```

Use native C# `TcpClient` for TCP port checks where possible.

#### Linux / Ubuntu

Use available commands, in fallback order:

```text
ip route get <ip>
tracepath <ip>
traceroute -n <ip>
```

Use native C# `TcpClient` for TCP port checks.

#### macOS

Use available commands, in fallback order:

```text
route -n get <ip>
netstat -rn
traceroute -n <ip>
```

Use native C# `TcpClient` for TCP port checks.

All external command wrappers must:

- Run asynchronously.
- Support cancellation.
- Enforce timeouts.
- Capture stdout and stderr.
- Never block the UI thread.
- Return structured objects, not raw strings only.

### 3. Add route misconfiguration diagnosis

Detect this case:

- Target is an RFC1918 private address.
- The current trace exits via `100.64.0.0/10` or a public IP after the local gateway.
- The app should explain: `This router does not know how to reach the target LAN subnet and is sending the packet upstream.`

Add helpers for:

- RFC1918 private range detection.
- `100.64.0.0/10` shared address space detection.
- Local subnet matching.
- Local L2 vs routed vs unreachable vs misrouted classification.

### 4. Add Remote Access Manager models

Add models in Core:

```text
RemoteAccessProfile
SshProfile
ConnectionCandidate
ConnectionTestResult
RemoteAccessRecommendation
TailscaleDevice
TailscaleStatus
SubnetRouteSuggestion
ConnectionMethod
```

Support methods:

```text
Direct LAN IP
Tailscale IP/name
Port-forward guidance
Manual network switch guidance
Unavailable
```

### 5. Add known-device remote access profiles

Update `known-devices.json` or equivalent config to support remote access fields.

Seed examples:

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
        "user": "gagneet",
        "port": 22
      },
      "tags": ["critical", "server"]
    },
    {
      "id": "fast5366lte-a",
      "displayName": "FAST5366LTE-A / Optus Modem",
      "deviceType": "Router",
      "knownIps": ["192.168.0.1"],
      "tags": ["optus", "router", "gateway"]
    },
    {
      "id": "google-nest",
      "displayName": "Google Nest Router / Mesh",
      "deviceType": "Router",
      "knownSubnets": ["192.168.87.0/24"],
      "tags": ["google", "nest", "mesh", "router"]
    },
    {
      "id": "eero-main",
      "displayName": "Eero Main Router",
      "deviceType": "Router",
      "knownSubnets": ["192.168.4.0/24"],
      "tags": ["eero", "upstream", "router"]
    }
  ]
}
```

Allow user-specific overrides without requiring source code changes.

### 6. Add Tailscale integration

Implement optional Tailscale support.

Detect whether Tailscale is installed:

```text
tailscale version
tailscale status --json
tailscale ip -4
```

Parse:

- Local Tailscale IP.
- Tailnet device names.
- Online/offline status.
- Tailscale IPv4 addresses.
- Hostnames and DNS names.
- Advertised subnet routes when available.

Map known critical devices to Tailscale devices by:

- configured Tailscale name,
- Tailscale IP,
- hostname,
- stable alias.

Do not require Tailscale to be installed. Show states:

```text
Not installed
Installed but not connected
Connected
Known server found
Known server not found
```

### 7. Add Subnet Router Assistant

Add logic to generate commands but do not run privileged setup automatically.

For Linux server on Google Nest subnet:

```bash
sudo tailscale up --advertise-routes=192.168.87.0/24
```

For a broader LAN gateway/sensor that can reach multiple networks:

```bash
sudo tailscale up --advertise-routes=192.168.0.0/24,192.168.87.0/24
```

Show explanatory guidance:

```text
Run this on an always-on Linux server or gateway device.
Approve the advertised subnet route in the Tailscale admin console.
Then use Tailscale from Windows, Ubuntu, or macOS to access the server or subnet remotely.
```

Warn:

```text
If the route trace shows 100.64.0.0/10, normal inbound port forwarding may fail because of CGNAT/shared address space. Prefer Tailscale or Cloudflare Tunnel.
```

### 8. Add SSH command generation and launch actions

Core should generate SSH commands.

Examples:

```text
ssh gagneet@192.168.87.243
ssh gagneet@192.168.0.5 -p 2222
ssh gagneet@home-server
ssh gagneet@100.x.y.z
```

If port is 22, omit `-p 22`.

Windows launcher:

```text
wt.exe ssh <user>@<host> -p <port>
powershell.exe -NoExit -Command "ssh <user>@<host> -p <port>"
```

Linux launcher:

```text
x-terminal-emulator -e ssh <user>@<host> -p <port>
gnome-terminal -- ssh <user>@<host> -p <port>
konsole -e ssh <user>@<host> -p <port>
```

macOS launcher:

```text
open -a Terminal
osascript can be used later if needed, but keep it simple initially.
```

CLI should always print the command even if launching is not supported.

### 9. Add CLI project

Create:

```text
src/LanInspector.Cli/LanInspector.Cli.csproj
```

Target:

```text
net8.0
```

Use a command-line parser such as `System.CommandLine` if stable in the project, or a simple parser if you want to avoid package risk.

CLI command examples:

```bash
laninspector status
laninspector interfaces
laninspector devices
laninspector known
laninspector check home-server
laninspector check-ip 192.168.87.243 --port 22
laninspector route 192.168.87.243
laninspector trace 192.168.87.243
laninspector ssh home-server --print
laninspector ssh home-server --open
laninspector tailscale status
laninspector tailscale routes
laninspector recommend home-server
laninspector capture-prereqs
```

Expected CLI output should be plain-English and useful.

Example:

```text
LanInspector Route Check

Current machine:
  OS: Windows
  Interface: Wi-Fi
  IP: 192.168.4.32
  Network: Eero / 192.168.4.0/24

Target:
  Home Server
  IP: 192.168.87.243
  SSH: 22

Result:
  Direct LAN SSH: Failed
  Trace: 192.168.4.1 -> 100.96.16.1 -> timeout

Diagnosis:
  Eero does not know how to reach the Google Nest subnet 192.168.87.0/24.
  It is sending the packet upstream instead of routing internally.

Recommended:
  1. Use Tailscale if configured:
     ssh gagneet@home-server
  2. Or connect to Optus Wi-Fi and use:
     ssh gagneet@192.168.87.243
```

### 10. Keep WPF but consume the same services

Update the existing WPF app to use the same Core services used by CLI.

Add or refine the WPF Remote Access tab:

Top cards:

```text
Current Network
Critical Devices
Direct LAN Access
Tailscale Access
Warnings
```

Main panels:

```text
Critical Devices list
Connection Matrix
Selected Device Detail
Recommended Action
Setup Assistant
```

Connection Matrix columns:

```text
Device
Target
Method: LAN / Tailscale / Guidance
Status
Route
SSH
Recommendation
```

### 11. Capture prerequisites by platform

Add `capture-prereqs` detection.

Windows:

- Npcap installed.
- Capture devices available.
- Friendly message if Npcap is missing.

Linux:

- libpcap available.
- User has permission or capability.
- Check whether running as root or whether executable has capture capability.
- Suggest:

```bash
sudo setcap cap_net_raw,cap_net_admin=eip ./laninspector
```

Only suggest commands. Do not run privilege changes automatically.

macOS:

- libpcap/BPF availability.
- Mention that packet capture may require admin privileges or permissions.
- CLI should still support route/Tailscale/SSH features even if packet capture is unavailable.

### 12. Publishing and packaging

Add scripts:

```text
scripts/publish-windows.ps1
scripts/publish-cli.ps1
scripts/publish-cli.sh
```

Windows WPF publish:

```powershell
dotnet publish .\src\LanInspector.UI\LanInspector.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -o .\artifacts\LanInspector-win-x64
```

CLI publish targets:

```bash
dotnet publish src/LanInspector.Cli/LanInspector.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/laninspector-cli-win-x64

dotnet publish src/LanInspector.Cli/LanInspector.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/laninspector-cli-linux-x64

dotnet publish src/LanInspector.Cli/LanInspector.Cli.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o artifacts/laninspector-cli-osx-arm64

dotnet publish src/LanInspector.Cli/LanInspector.Cli.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/laninspector-cli-osx-x64
```

Zip each output folder.

### 13. GitHub Actions

Add or update workflow:

```text
.github/workflows/build-and-publish.yml
```

Jobs:

1. Windows WPF build and publish.
2. CLI build and publish for:
   - win-x64
   - linux-x64
   - osx-x64
   - osx-arm64
3. Upload artifacts.

Use `windows-latest` for WPF.
Use matrix builds for CLI if appropriate.

### 14. README updates

Update README with:

- WPF Windows UI usage.
- CLI usage.
- Remote Access Manager.
- Tailscale integration.
- SSH actions.
- Cross-platform status.
- Capture prerequisites per OS.
- Publishing instructions.
- Security notes: no router passwords, no SSH passwords.

### 15. Tests

Add unit tests in Core for:

- RFC1918 private IP detection.
- `100.64.0.0/10` shared address detection.
- SSH command generation.
- Known device matching.
- Tailscale JSON parsing using sample status JSON.
- Connection recommendation logic.
- Route diagnosis classification.

Manual validation:

Windows:

```powershell
dotnet build LanInspector.sln -c Release
.\scripts\publish-windows.ps1
.\scripts\publish-cli.ps1
.\artifacts\laninspector-cli-win-x64\laninspector.exe status
.\artifacts\laninspector-cli-win-x64\laninspector.exe check home-server
```

Ubuntu:

```bash
dotnet build LanInspector.sln -c Release
./scripts/publish-cli.sh
./artifacts/laninspector-cli-linux-x64/laninspector status
./artifacts/laninspector-cli-linux-x64/laninspector route 192.168.87.243
./artifacts/laninspector-cli-linux-x64/laninspector tailscale status
```

macOS:

```bash
./artifacts/laninspector-cli-osx-arm64/laninspector status
./artifacts/laninspector-cli-osx-arm64/laninspector route 192.168.87.243
./artifacts/laninspector-cli-osx-arm64/laninspector ssh home-server --print
```

Definition of done:

- `dotnet build LanInspector.sln -c Release` passes on Windows.
- Core tests pass.
- WPF app still runs on Windows.
- CLI builds for Windows, Linux, and macOS targets.
- CLI can print route diagnostics and SSH recommendations without packet capture.
- Windows publish script creates a runnable WPF executable artifact.
- CLI publish scripts create self-contained artifacts.
- README documents all commands and prerequisites.
- No passwords are stored.

## Implementation order

1. Add platform abstractions.
2. Add CLI project with basic `status`, `route`, `check-ip`, and `ssh --print`.
3. Move route and remote-access recommendation logic into Core.
4. Add Windows implementation.
5. Add Linux/macOS command wrappers.
6. Add Tailscale service.
7. Add WPF Remote Access tab enhancements using the same services.
8. Add publish scripts.
9. Add GitHub Actions artifacts.
10. Update README.

## Deferred items

Do not include these in this phase unless the existing architecture makes them trivial:

- Avalonia UI migration.
- Nmap integration.
- SNMP/LLDP topology.
- Full topology graph.
- Pi-hole/AdGuard DNS integration.
- Full traffic-flow charts.
- Automatic router login/configuration.

These should remain future phases after the CLI and Remote Access Manager are stable.
