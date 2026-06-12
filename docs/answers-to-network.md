Yes — I agree with that assessment and the proposed milestone. I also tried to fetch `docs/enhance-network-sniffer.md` from the repo, but GitHub returned `404 Not Found`, so either the file is not on `master`, the path/name differs, or it has not been pushed yet. Based on the content you pasted, the direction is correct.

The external checks are reasonable: Sniffnet is a strong UX benchmark for adapter selection, filters, real-time traffic views and enrichment; AdGuard Home and Pi-hole are better future DNS integrations than building DNS into LanInspector itself; Avalonia is a sensible later cross-platform option because it is a .NET cross-platform UI framework for Windows, Linux and macOS. ([Wikipedia][1])

## Answers to the questions

### 1. Should the app remain Windows-first WPF for the next few PRs?

**Yes. Keep WPF for the next few PRs.**

Do not migrate to Avalonia yet. The app still needs its core product behaviour stabilised: device identity, route-aware reachability, known devices, device actions and details UX. Migrating UI now will slow down the actual network intelligence work.

But I would start shaping the boundaries now:

```text
LanInspector.Core
  device model, analyzers, route model, known devices, scan logic

LanInspector.Windows
  WPF UI, Windows Terminal integration, PowerShell route wrappers

LanInspector.Abstractions
  interfaces for route checks, terminal launchers, platform services
```

That gives you a clean later path to:

```text
LanInspector.Avalonia
```

without rewriting the core. So: **stay WPF, but stop putting business logic into WPF view models wherever possible.**

---

### 2. Should `known-devices.json` seed your actual examples?

**Yes. Seed them, but make them editable and clearly marked as examples/default local config.**

Use your real current examples because they are exactly what the app is being built to solve.

Suggested seed:

```json
{
  "knownDevices": [
    {
      "id": "home-server",
      "displayName": "Home Server",
      "deviceType": "Server",
      "knownIps": [
        "192.168.0.148",
        "192.168.87.243"
      ],
      "ssh": {
        "enabled": true,
        "user": "gagneet",
        "port": 22
      },
      "tags": [
        "critical",
        "server"
      ]
    },
    {
      "id": "fast5366lte-a",
      "displayName": "FAST5366LTE-A / Optus Modem",
      "deviceType": "Router",
      "knownIps": [
        "192.168.0.1"
      ],
      "expectedVendor": "Sagemcom Broadband SAS",
      "tags": [
        "router",
        "gateway",
        "optus"
      ]
    },
    {
      "id": "google-nest",
      "displayName": "Google Nest Router / Mesh",
      "deviceType": "Router",
      "knownSubnets": [
        "192.168.87.0/24"
      ],
      "tags": [
        "router",
        "mesh",
        "google-nest"
      ]
    },
    {
      "id": "eero-main",
      "displayName": "Eero Main Router",
      "deviceType": "Router",
      "tags": [
        "router",
        "eero",
        "upstream"
      ]
    }
  ]
}
```

Do not commit your personal SSH keys, passwords, or anything secret. SSH username is fine if you are comfortable with it, but it should be easy to edit.

I would load config in this order:

```text
App defaults
User config file
Runtime discoveries
Manual aliases from UI
```

---

### 3. Are PowerShell wrappers okay initially for route checks?

**Yes. Use PowerShell wrappers initially, but isolate them behind an interface.**

For Windows-first WPF, this is acceptable and pragmatic. You need route answers quickly, not a perfect native networking abstraction.

Use wrappers for:

```powershell
Find-NetRoute -RemoteIPAddress <ip>
Test-NetConnection <ip> -Port <port>
tracert -d <ip>
```

But implement them behind something like:

```csharp
public interface IRouteDiagnosticsService
{
    Task<RouteDecision> GetRouteToAsync(IPAddress target, CancellationToken ct);
    Task<PortReachability> TestPortAsync(IPAddress target, int port, CancellationToken ct);
    Task<TraceRouteResult> TraceRouteAsync(IPAddress target, CancellationToken ct);
}
```

Then have:

```text
WindowsPowerShellRouteDiagnosticsService
NativeWindowsRouteDiagnosticsService later
LinuxRouteDiagnosticsService later
MacRouteDiagnosticsService later
```

This avoids locking the app design to PowerShell.

For port checks, prefer native C# `TcpClient` over `Test-NetConnection` because it is faster and easier to control. Use PowerShell mainly for route diagnostics.

---

### 4. Should active checks run automatically for known critical devices?

**Yes, but only lightweight checks should run automatically. Heavier scans must stay user-triggered.**

Recommended behaviour:

#### Automatic, safe, low-frequency

Run for known critical devices every 30–60 seconds while capture is active:

```text
TCP connect check to configured critical ports, e.g. 22 for SSH
Ping, optional
Last-seen update
Current best IP selection
Route summary refresh, maybe less frequently
```

For your Home Server:

```text
Check 192.168.0.148:22
Check 192.168.87.243:22
Show whichever is reachable
```

#### User-triggered only

Keep these behind buttons:

```text
Scan Common Ports
Deep scan
Trace route
Nmap later
Service/version detection
Subnet sweep
```

That is the right UX balance: the app remains helpful without unexpectedly scanning the whole LAN.

Suggested labels:

```text
Auto health check: On/Off
Last checked: 13:42:11
Known service: SSH 22 online
```

---

### 5. Should “Open SSH” launch Windows Terminal/PowerShell or only copy/show the command?

**Do both, with safe defaults.**

Recommended UX:

```text
Primary button: Copy SSH Command
Secondary button: Open SSH
Dropdown:
  Open in Windows Terminal
  Open in PowerShell
  Open in Command Prompt
```

Default to **show/copy command first**, because it is transparent and safe.

Once the user clicks “Open SSH”, launch Windows Terminal if available:

```csharp
Process.Start(new ProcessStartInfo
{
    FileName = "wt.exe",
    Arguments = $"ssh gagneet@192.168.87.243",
    UseShellExecute = true
});
```

Fallback to PowerShell:

```csharp
Process.Start(new ProcessStartInfo
{
    FileName = "powershell.exe",
    Arguments = "-NoExit -Command \"ssh gagneet@192.168.87.243\"",
    UseShellExecute = true
});
```

Show the exact command before launching:

```text
ssh gagneet@192.168.87.243
```

Do not store passwords. Let normal SSH key handling / Windows OpenSSH / agent handle authentication.

---

## My preferred default for the next PR

I would proceed with your default assumption, slightly refined:

```text
Keep WPF.
Strengthen Core/UI boundaries now.
Use PowerShell wrappers only for route diagnostics.
Use native C# for TCP port checks.
Make heavy active scans user-triggered.
Allow lightweight automatic checks for known critical devices.
Add both Copy SSH Command and Open SSH.
```

## Recommended next PR title

```text
[feature] add route-aware reachability and critical device actions
```

## Recommended exact scope

```text
1. Add local interface and subnet model.
2. Add gateway detection.
3. Add per-device Segment, Reachability, Gateway and RouteSummary.
4. Add known-devices.json with Home Server, FAST5366LTE-A, Google Nest and Eero examples.
5. Add Critical Devices panel.
6. Auto-check critical device configured ports, especially SSH 22.
7. Rename Scan Ports to Scan Common Ports.
8. Add device detail side panel.
9. Add Copy SSH Command and Open SSH action.
10. Keep topology graph, DNS integration and traffic-flow charts for later PRs.
```

The route-aware + known-device work will make the app immediately useful for your actual problem: “I am on `192.168.0.75`, the server is on `192.168.87.243`, can I reach it, and how do I connect?”

[1]: https://en.wikipedia.org/wiki/Avalonia_%28software_framework%29?utm_source=chatgpt.com "Avalonia (software framework)"

