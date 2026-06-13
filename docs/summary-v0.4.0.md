# **Work Completed in This PR**

## **Architecture**

- Extracted platform boundaries correctly: **Core** holds all interfaces and cross‑platform logic; platform‑specific code lives in `Platform.*` assemblies.  
- `LanInspector.UI` (WPF) now depends on `Platform.Windows` instead of having its own inline service copies.  
- `LanInspector.Cli` references all three platform projects and picks the right one at runtime — a **single binary** that works on Windows, Linux, and macOS.

---

# **Core (Platform‑Neutral)**

## **Added Components**

| Component                                   | Purpose                                                                 |
|---------------------------------------------|-------------------------------------------------------------------------|
| IRouteDiagnosticsService                    | Already existed — now the canonical contract                           |
| ITerminalLauncher                           | Async SSH terminal launch abstraction                                   |
| ICapturePrerequisiteService                 | Capture driver/permission check abstraction                             |
| ProcessHelper                               | Shared async process runner with timeout + cancellation                 |
| RouteHelpers                                | RFC1918, CGNAT/Tailscale range detection, route misconfiguration diagnosis |
| SshCommandGenerator                         | Generates correct `ssh [-p port] user@host` strings                     |
| TailscaleCliService                         | Parses `tailscale status --json`, cross‑platform                        |
| ITailscaleService + models                  | TailscaleStatus, TailscaleDevice, TailscaleConnectionState              |
| RemoteAccessRecommendationEngine            | Tests direct LAN + Tailscale candidates, surfaces CGNAT warning         |
| SubnetRouteAssistant                        | Generates `tailscale up --advertise-routes=...` commands                |
| KnownDeviceDefinition.KnownTailscaleNames   | Matches known devices to Tailscale peers by name                        |

---

# **Platform Implementations**

| Project            | Implementations                                                                 |
|--------------------|----------------------------------------------------------------------------------|
| **Platform.Windows** | WindowsRouteDiagnosticsService (Find‑NetRoute + tracert), WindowsTerminalLauncher (wt.exe / powershell), WindowsCapturePrerequisiteService (Npcap DLL check) |
| **Platform.Linux**   | LinuxRouteDiagnosticsService (`ip route` + tracepath fallback), LinuxTerminalLauncher (gnome‑terminal / konsole / xterm), LinuxCapturePrerequisiteService (libpcap + getcap + `/proc/self/status`) |
| **Platform.MacOS**   | MacOsRouteDiagnosticsService (`route -n get` + traceroute), MacOsTerminalLauncher (`open -a Terminal`), MacOsCapturePrerequisiteService (BPF device check) |

---

# **CLI (laninspector)**

**All commands implemented and compile‑verified:**

```
status, interfaces, known,
check <id>, check-ip <ip> [--port],
route <ip>, trace <ip>,
ssh <id> [--print|--open],
tailscale status, tailscale routes,
recommend <id>, capture-prereqs
```

---

# **Tests**

**39 unit tests**, all passing on Linux:

- RFC1918 / CGNAT / public‑internet classification  
- Route misconfiguration detection (Eero → 100.64.x.x egress)  
- SSH command generation (port 22 omission, non‑standard port)  
- Known device config loading and merge‑by‑id  
- Tailscale JSON parsing (connected, not‑running, not‑installed)

---

# **Infra**

- `scripts/publish-windows.ps1` — single‑file WPF artifact  
- `scripts/publish-cli.ps1` + `publish-cli.sh` — all 4 CLI targets (win‑x64, linux‑x64, osx‑x64, osx‑arm64)  
- `.github/workflows/build-and-publish.yml` — CI matrix; Linux builds Core+CLI+Tests, Windows builds full solution+WPF, matrix job covers all 4 CLI publish targets  

---

# **Deferred Items — Status and What’s Needed**

## **1. Avalonia Cross‑Platform GUI**

**Status:** Intentionally deferred — architecture is now ready.

**What’s needed:**

- New project `LanInspector.Avalonia` targeting net8.0  
- Avalonia packages: `Avalonia`, `Avalonia.Desktop`, `Avalonia.ReactiveUI` or `Avalonia.CommunityToolkit`  
- Port XAML views from WPF → Avalonia AXAML  
- Replace `Clipboard.SetText` with `TopLevel.GetTopLevel(this)?.Clipboard`  
- Platform services already abstracted — wire via `PlatformServiceFactory`  
- This is a **full UI rewrite**, not a small change  

---

## **2. Nmap Integration**

**Status:** Not started.

**What’s needed:**

- Interface `IPortScanService` in Core  
- Platform implementation shelling out to `nmap` with XML parsing (`-oX -`)  
- OS‑specific detection of Nmap availability  
- WPF + CLI commands for Nmap scans  
- Permission note: SYN scans require root/admin; connect scans do not  

---

## **3. SNMP/LLDP Topology**

**Status:** Not started — significant protocol addition.

**What’s needed:**

- SNMP: NuGet package (e.g., `Lextm.SharpSnmpLib`)  
- LLDP: passive capture via SharpPcap (EtherType `0x88CC`)  
- Models: `SnmpDevice`, `LldpNeighbour`, `TopologyLink`  
- SNMP walk for standard MIBs:  
  - `sysDescr`, `ifTable`, `ipAddrTable`, `dot1dTpFdbTable`  
- Enables switch‑port mapping + neighbour discovery  

---

## **4. Full Topology Graph**

**Status:** Not started — depends on SNMP/LLDP.

**What’s needed:**

- Graph data model in Core: nodes + edges (L2/L3/Tailscale)  
- WPF: canvas renderer or GraphX/OxyPlot  
- Avalonia: same in future  
- CLI: text‑based topology summary (`laninspector topology`)  
- Data sources: ARP table, LLDP neighbours, SNMP MAC tables, route info  

---

## **5. Pi‑hole / AdGuard Integration**

**Status:** Not started.

**What’s needed:**

- Pi‑hole REST API: `/api/queries`, `/api/summary` (v5) or FTL API (v6)  
- AdGuard Home REST API: `/control/querylog`, `/control/stats`  
- Interface `IDnsFilterService` in Core  
- Config: URL + API token (no passwords in code)  
- Use cases: top clients, blocked queries, per‑device DNS activity  

---

## **6. Full Traffic‑Flow Charts**

**Status:** Partially ground‑worked (packet capture exists).

**What’s needed:**

- Aggregation layer in Core: bytes/packets per device per time window  
- WPF: chart control (LiveChartsCore or OxyPlot)  
- Avalonia: LiveChartsCore.Avalonia  
- Per‑device breakdown: upload/download, protocol distribution  
- BPF filter presets already exist; analyzers need to emit byte‑count events  

---

# **Recommended Implementation Order**

1. **Avalonia GUI** — architecture ready; unlocks Linux/macOS UI  
2. **Traffic‑flow charts** — packet data already available  
3. **Nmap** — straightforward, high user value  
4. **Pi‑hole/AdGuard** — useful DNS visibility  
5. **SNMP/LLDP + topology graph** — most complex, protocol + UI work  

---