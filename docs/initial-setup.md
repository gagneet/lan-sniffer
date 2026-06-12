Your issue sounds like the server is getting DHCP from **two possible networks**:

| Network          | Likely DHCP source             | Server IP example |
| ---------------- | ------------------------------ | ----------------- |
| `192.168.0.xxx`  | Optus FAST5366LTE-A / LAN side | `192.168.0.148`   |
| `192.168.87.xxx` | Google Nest network            | `192.168.87.243`  |

That usually happens when the server is connected in a way that can reach both DHCP domains, or when Google Nest is still acting as a router/NAT network rather than pure bridge/AP mode.

## Immediate goal: find where the server is now

From your Windows PC, run these in **PowerShell**.

### 1. Check which network your PC is currently on

```powershell
ipconfig
```

Look for your active Wi-Fi adapter.

You will see something like:

```text
IPv4 Address . . . . . . . . . . : 192.168.87.xxx
Default Gateway . . . . . . . . : 192.168.87.1
```

or:

```text
IPv4 Address . . . . . . . . . . : 192.168.0.xxx
Default Gateway . . . . . . . . : 192.168.0.1
```

If your PC is on `192.168.87.xxx`, you are on the Google Nest network.

If your PC is on `192.168.0.xxx`, you are on the Optus/FAST5366LTE-A LAN network.

---

## 2. Ping both possible server IPs

```powershell
ping 192.168.0.148
ping 192.168.87.243
```

Then check ARP:

```powershell
arp -a
```

Look for either:

```text
192.168.0.148
192.168.87.243
```

If ping fails but ARP shows a MAC address, the device may be present but blocking ping.

---

## 3. Check whether SSH is reachable

Use this:

```powershell
Test-NetConnection 192.168.0.148 -Port 22
Test-NetConnection 192.168.87.243 -Port 22
```

The important line is:

```text
TcpTestSucceeded : True
```

If `True`, SSH should work.

Then connect:

```powershell
ssh username@192.168.0.148
```

or:

```powershell
ssh username@192.168.87.243
```

Replace `username` with your server user, for example:

```powershell
ssh gagneet@192.168.0.148
```

---

# Most likely reason SSH fails

If your PC is on Google Nest `192.168.87.xxx`, and the server is on Optus `192.168.0.148`, then you may not be able to reach it directly depending on routing/NAT/firewall.

Likewise, if the server moved to `192.168.87.243` but your PC is on `192.168.0.xxx`, the Optus side may not know how to reach the Google Nest internal subnet.

This is a **network segmentation / double-NAT problem**, not an SSH problem.

---

# Fastest practical fix

## Option A — connect your PC to the same network as the server

If the server is currently:

```text
192.168.0.148
```

connect your PC to the FAST5366LTE-A / Optus LAN side and SSH to:

```powershell
ssh username@192.168.0.148
```

If the server is currently:

```text
192.168.87.243
```

connect your PC to Google Nest Wi-Fi and SSH to:

```powershell
ssh username@192.168.87.243
```

This is the quickest way to confirm access.

---

# Better long-term fix

You should stop the server from moving between networks.

## Recommended setup

Pick **one LAN subnet** for servers. I would use the Optus/FAST5366LTE-A LAN side if the switch and servers are physically connected there:

```text
Servers: 192.168.0.xxx
Switch: behind FAST5366LTE-A
Google Nest: bridge/AP mode if possible
Eero: main internet router
```

Then give the server a static/reserved IP:

```text
Server: 192.168.0.148
Gateway: 192.168.0.1
Subnet mask: 255.255.255.0
DNS: 192.168.0.1 or 1.1.1.1 / 8.8.8.8
```

Or, if Google Nest must remain the main LAN for Wi-Fi clients, put the server on:

```text
Server: 192.168.87.243
Gateway: 192.168.87.1
Subnet mask: 255.255.255.0
DNS: 192.168.87.1 or public DNS
```

But do **not** let it float between both.

---

# On the server, confirm its current IP

If you have local keyboard/monitor access to the server, or can access it another way, run:

## Linux

```bash
ip addr
ip route
hostname -I
```

Also check SSH:

```bash
sudo systemctl status ssh
sudo ss -tlnp | grep :22
```

If SSH is not installed/running:

```bash
sudo apt update
sudo apt install openssh-server
sudo systemctl enable --now ssh
```

## Windows Server

```powershell
ipconfig
Get-NetIPAddress -AddressFamily IPv4
Get-NetRoute -DestinationPrefix "0.0.0.0/0"
```

---

# Useful scan from your PC

From PowerShell:

```powershell
for ($i=1; $i -le 254; $i++) {
  Test-Connection "192.168.0.$i" -Count 1 -Quiet | Out-Null
}
arp -a | findstr "192.168.0."
```

And for Google Nest network:

```powershell
for ($i=1; $i -le 254; $i++) {
  Test-Connection "192.168.87.$i" -Count 1 -Quiet | Out-Null
}
arp -a | findstr "192.168.87."
```

Then test SSH on likely server IPs:

```powershell
Test-NetConnection 192.168.0.148 -Port 22
Test-NetConnection 192.168.87.243 -Port 22
```

---

# Small C# utility idea for your exact problem

Before building the full network-sniffing app, build a simple **“Where is my server?”** tool.

It should:

1. Scan known subnets:

   * `192.168.0.0/24`
   * `192.168.87.0/24`

2. Test likely server IPs:

   * `192.168.0.148`
   * `192.168.87.243`

3. Check port 22.

4. Display:

```text
Server found at: 192.168.87.243
SSH status: Open
Connect using: ssh username@192.168.87.243
Detected from local interface: Wi-Fi 192.168.87.115
```

5. Warn:

```text
Your PC is on 192.168.87.xxx.
The server is on 192.168.0.xxx.
SSH may fail unless routing exists between these networks.
```

That would solve your immediate pain before building the bigger application.




---------------------------

Further elaboration, where we want a cross-network/router application, without having to connect to the other networks:

You are correct: your case proves this is **not simply “same subnet or no access”**.

Your PC:

```text
PC Wi-Fi IP:       192.168.0.75
Current subnet:    192.168.0.0/24
Likely gateway:    192.168.0.1
```

Server:

```text
Server IP:         192.168.87.243
Server subnet:     192.168.87.0/24
Likely gateway:    192.168.87.1
```

Yet SSH works from `192.168.0.75` to `192.168.87.243`, which means one of these is true:

1. The Google Nest network allows inbound routing/NAT from the `192.168.0.0/24` side.
2. The Optus/FAST5366LTE-A has a route to `192.168.87.0/24`.
3. Google Nest is not fully isolated and is forwarding traffic between the upstream and downstream networks.
4. Your SSH path is going through an existing route table entry on your Windows machine or router.
5. The server has multiple interfaces, one on each network, but you are currently connecting to the `192.168.87.243` address.

The important point: **ARP will not show `192.168.87.243` from your `192.168.0.75` interface**, because ARP only resolves devices on the directly connected local subnet. For remote subnets, Windows ARPs for the gateway, usually `192.168.0.1`, and sends the traffic there.

So your utility should not rely only on ARP. It needs to combine:

```text
Local interface detection
Route table inspection
Gateway detection
Ping/TCP port checks
Traceroute
ARP for same-subnet only
SSH port test
Optional Nmap scan
Optional mDNS/SSDP discovery
Optional router API enrichment
```

---

# Immediate commands to confirm how SSH is reaching the server

Run these from your Windows machine.

## 1. Confirm route path to the server

```powershell
tracert 192.168.87.243
```

You may see something like:

```text
1    192.168.0.1
2    192.168.87.243
```

or:

```text
1    192.168.0.1
2    192.168.87.1
3    192.168.87.243
```

This will tell you which device is forwarding traffic.

## 2. Check Windows route decision

```powershell
route print
```

Or more targeted:

```powershell
Get-NetRoute -AddressFamily IPv4 | Sort-Object RouteMetric | Format-Table ifIndex,DestinationPrefix,NextHop,RouteMetric,InterfaceAlias
```

You are looking for whether Windows has a route for:

```text
192.168.87.0/24
```

or whether it is using the default route:

```text
0.0.0.0/0 via 192.168.0.1
```

## 3. Confirm SSH availability

```powershell
Test-NetConnection 192.168.87.243 -Port 22
```

Expected good result:

```text
TcpTestSucceeded : True
```

## 4. Confirm the server is not directly ARP-visible

This is expected to fail or not show the server:

```powershell
arp -a | findstr 192.168.87.243
```

That does **not** mean the server is unreachable. It only means it is not on the same Layer-2 segment as your current interface.

---

# What your utility should do for this exact network

The utility should answer this kind of question:

```text
Where is my server currently?
Can I reach it?
Which network am I on?
Which gateway is being used?
Can I SSH to it?
What command should I use?
Is the device local, routed, NATed, or unreachable?
```

For your example, it should display something like:

```text
Current PC network:
  Interface: Wi-Fi
  IP: 192.168.0.75
  Subnet: 192.168.0.0/24
  Gateway: 192.168.0.1

Known server:
  Name: Home Server
  Last known IPs:
    192.168.0.148
    192.168.87.243

Current status:
  192.168.0.148   Not reachable on SSH
  192.168.87.243  Reachable on SSH

SSH:
  ssh username@192.168.87.243

Path:
  192.168.0.75 -> 192.168.0.1 -> 192.168.87.243

Interpretation:
  Server is currently on Google/Nest subnet 192.168.87.0/24.
  Your PC is on Optus/FAST5366LTE-A subnet 192.168.0.0/24.
  Routing between the two networks is currently working.
```

---

# Revised application design

Your app should have two modes:

1. **Server locator mode**
2. **Network intelligence mode**

For your immediate pain, build **Server Locator Mode** first.

## Server Locator Mode

Inputs:

```text
Known server names
Known server IPs
Known SSH usernames
Known SSH ports
Known subnets
```

Example config:

```json
{
  "servers": [
    {
      "name": "Home Server",
      "sshUser": "gagneet",
      "sshPort": 22,
      "knownIps": [
        "192.168.0.148",
        "192.168.87.243"
      ],
      "knownHostnames": [
        "homeserver",
        "homeserver.local"
      ]
    }
  ],
  "knownSubnets": [
    "192.168.0.0/24",
    "192.168.87.0/24"
  ]
}
```

The app should test each known address:

```text
Ping test
TCP 22 test
SSH banner test
Reverse DNS
mDNS hostname
Traceroute path
Route decision
```

The winning IP is the one where SSH port is open.

---

# Why ARP alone is insufficient

Your ARP result shows this:

```text
Interface: 192.168.0.75

192.168.0.1
192.168.0.5
192.168.0.6
192.168.0.7
192.168.0.8
192.168.0.11
192.168.0.12
192.168.0.129
```

These are all on:

```text
192.168.0.0/24
```

That is correct.

`192.168.87.243` is not listed because it belongs to:

```text
192.168.87.0/24
```

From your PC’s perspective, `192.168.87.243` is a routed destination, not a local ARP destination.

So the utility needs this logic:

```text
If target IP is in same subnet:
    use ARP + ping + TCP check
Else:
    use route table + gateway + traceroute + TCP check
```

---

# C# utility architecture for your case

## Core modules

```text
NetworkInterfaceService
RouteTableService
TargetProbeService
SshProbeService
TraceRouteService
ArpTableService
KnownServerRegistry
ResultAggregator
UI Dashboard
```

## Data flow

```text
Read local interfaces
        |
Read route table
        |
Load known servers
        |
For each known IP:
    Check whether same subnet
    Check route
    Ping
    Test TCP 22
    Try SSH banner
    Run traceroute
        |
Score result
        |
Show best SSH target
```

---

# MVP screen layout

## Current Network

```text
Current Interface: Wi-Fi
IP Address:        192.168.0.75
Subnet:            192.168.0.0/24
Gateway:           192.168.0.1
DNS:               192.168.0.1
Network Type:      Optus/FAST5366LTE-A side
```

## Known Server Status

| Server      |               IP | Same subnet? | Ping | SSH 22 | Route             | Recommendation |
| ----------- | ---------------: | -----------: | ---: | -----: | ----------------- | -------------- |
| Home Server |  `192.168.0.148` |          Yes | Fail |   Fail | Local             | Not current    |
| Home Server | `192.168.87.243` |           No | Pass |   Open | via `192.168.0.1` | Use this       |

## SSH Command

```bash
ssh gagneet@192.168.87.243
```

## Path

```text
192.168.0.75
   |
192.168.0.1
   |
192.168.87.243
```

---

# C# MVP code skeleton

This is the logic I would start with.

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public sealed record ServerTarget(
    string Name,
    string SshUser,
    int SshPort,
    IReadOnlyList<IPAddress> KnownIps
);

public sealed record ProbeResult(
    string ServerName,
    IPAddress Ip,
    bool IsSameSubnet,
    bool PingSucceeded,
    bool TcpPortOpen,
    string? RecommendedSshCommand,
    string Notes
);

public static class ServerLocator
{
    public static async Task<IReadOnlyList<ProbeResult>> ProbeServerAsync(ServerTarget target)
    {
        var results = new List<ProbeResult>();

        foreach (var ip in target.KnownIps)
        {
            var sameSubnet = IsOnAnyLocalSubnet(ip);
            var pingOk = await PingAsync(ip);
            var tcpOpen = await TcpPortOpenAsync(ip, target.SshPort, TimeSpan.FromSeconds(2));

            var command = tcpOpen
                ? $"ssh {target.SshUser}@{ip}"
                : null;

            var notes = tcpOpen
                ? "SSH is reachable. Use this address."
                : sameSubnet
                    ? "Same subnet but SSH is not reachable."
                    : "Different subnet and SSH is not reachable from current network.";

            results.Add(new ProbeResult(
                target.Name,
                ip,
                sameSubnet,
                pingOk,
                tcpOpen,
                command,
                notes
            ));
        }

        return results
            .OrderByDescending(r => r.TcpPortOpen)
            .ThenByDescending(r => r.PingSucceeded)
            .ToList();
    }

    private static async Task<bool> PingAsync(IPAddress ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1500);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TcpPortOpenAsync(IPAddress ip, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOnAnyLocalSubnet(IPAddress targetIp)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var ipProps = nic.GetIPProperties();

            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (unicast.IPv4Mask is null)
                    continue;

                if (IsInSameSubnet(targetIp, unicast.Address, unicast.IPv4Mask))
                    return true;
            }
        }

        return false;
    }

    private static bool IsInSameSubnet(IPAddress address, IPAddress networkAddress, IPAddress subnetMask)
    {
        var ip = address.GetAddressBytes();
        var network = networkAddress.GetAddressBytes();
        var mask = subnetMask.GetAddressBytes();

        for (var i = 0; i < ip.Length; i++)
        {
            if ((ip[i] & mask[i]) != (network[i] & mask[i]))
                return false;
        }

        return true;
    }
}
```

Example usage:

```csharp
var server = new ServerTarget(
    Name: "Home Server",
    SshUser: "gagneet",
    SshPort: 22,
    KnownIps: new[]
    {
        IPAddress.Parse("192.168.0.148"),
        IPAddress.Parse("192.168.87.243")
    }
);

var results = await ServerLocator.ProbeServerAsync(server);

foreach (var result in results)
{
    Console.WriteLine($"{result.ServerName} {result.Ip}");
    Console.WriteLine($"  Same subnet: {result.IsSameSubnet}");
    Console.WriteLine($"  Ping:        {result.PingSucceeded}");
    Console.WriteLine($"  SSH open:    {result.TcpPortOpen}");
    Console.WriteLine($"  Command:     {result.RecommendedSshCommand ?? "N/A"}");
    Console.WriteLine($"  Notes:       {result.Notes}");
}
```

---

# Add route detection

The next improvement is to inspect how Windows routes to `192.168.87.243`.

The simplest MVP method is to call PowerShell/Windows commands and parse them.

```csharp
public static async Task<string> RunCommandAsync(string fileName, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("Could not start process.");

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    return string.IsNullOrWhiteSpace(output) ? error : output;
}
```

For route info:

```csharp
var routeInfo = await RunCommandAsync(
    "powershell",
    "-NoProfile -Command \"Find-NetRoute -RemoteIPAddress 192.168.87.243\""
);

Console.WriteLine(routeInfo);
```

For traceroute:

```csharp
var trace = await RunCommandAsync("tracert", "-d 192.168.87.243");
Console.WriteLine(trace);
```

This gives you the most important answer:

```text
Which gateway is used to reach the server?
```

---

# Better server identification

Testing only IPs is useful, but not enough. The app should identify the server more robustly.

Use multiple identity signals:

```text
Known IPs
Known hostnames
Known MAC addresses
SSH host key fingerprint
mDNS name
HTTP admin page title if any
Open service profile
```

For your server, save something like:

```json
{
  "name": "Home Server",
  "knownIps": ["192.168.0.148", "192.168.87.243"],
  "knownMacs": ["aa-bb-cc-dd-ee-ff"],
  "sshUser": "gagneet",
  "sshPort": 22,
  "sshHostKeyFingerprint": "SHA256:xxxxxxxx"
}
```

The **SSH host key fingerprint** is very useful because even if the IP changes, you can confirm it is the same machine.

---

# Fixing the underlying network problem

The utility helps you find the server, but the better long-term fix is to stop the server moving between networks.

You should choose one of these designs.

## Option 1 — keep server permanently on Optus/FAST5366LTE-A LAN

Use this if your switch and server are physically connected to the FAST5366LTE-A side.

```text
Server IP:    192.168.0.148
Gateway:      192.168.0.1
Subnet:       255.255.255.0
DNS:          192.168.0.1 or 1.1.1.1
```

Then set either:

* DHCP reservation on the Optus/FAST5366LTE-A, or
* static IP on the server.

This is probably the cleanest setup if the server is wired to the TP-Link switch.

## Option 2 — keep server permanently on Google Nest LAN

Use this if most clients connect through Google Nest Wi-Fi.

```text
Server IP:    192.168.87.243
Gateway:      192.168.87.1
Subnet:       255.255.255.0
DNS:          192.168.87.1 or 1.1.1.1
```

Then reserve `192.168.87.243` in Google/Nest DHCP.

## Option 3 — put Google Nest into bridge/AP mode

This is often the best home-network simplification, but with mesh systems it depends on what features you need.

Target result:

```text
Everything uses one LAN subnet only:
192.168.0.0/24 or whatever Eero uses

No separate 192.168.87.0/24 network
No double NAT
No server switching
```

Then your network becomes easier:

```text
NBN / Origin
   |
Eero main router
   |
FAST5366LTE-A acting as switch
   |
Google Nest bridge/AP mode
   |
Clients all on same subnet
```

However, Google/Nest bridge mode may disable or limit some mesh/router features depending on model and configuration.

---

# What your utility should specifically support

Given your environment, I would make these first-class concepts in the app:

## 1. Known networks

```text
Eero / upstream network
Optus / FAST5366LTE-A LAN: 192.168.0.0/24
Google Nest LAN: 192.168.87.0/24
```

But keep them configurable, not hardcoded.

## 2. Known infrastructure devices

```text
Eero main router
FAST5366LTE-A
Google Nest router
Google Nest mesh points
TP-Link switch
```

## 3. Known critical devices

```text
Home Server
NAS
Printer
Cameras
Smart home hubs
```

## 4. Reachability matrix

This is very useful in your case:

| From current PC network |        To target | Ping |  SSH |  HTTP | Route  |
| ----------------------- | ---------------: | ---: | ---: | ----: | ------ |
| `192.168.0.75`          |  `192.168.0.148` | Fail | Fail |  Fail | Local  |
| `192.168.0.75`          | `192.168.87.243` | Pass | Pass | Maybe | Routed |

## 5. “Connect now” command

For each server:

```text
Best SSH address:
ssh gagneet@192.168.87.243
```

## 6. Explanation

The app should explain:

```text
You are currently on 192.168.0.0/24.
The server is currently reachable on 192.168.87.243.
This is a routed connection, not same-subnet ARP discovery.
ARP will not show the server from this interface.
```

That explanation alone will save debugging time.

---

# Practical build plan

## Build this first: Console MVP

Before UI, build a console app called:

```text
NetworkWhere
```

Command:

```bash
networkwhere server
```

Output:

```text
Current interface:
  Wi-Fi 192.168.0.75/24
  Gateway: 192.168.0.1

Checking Home Server:
  192.168.0.148
    Same subnet: yes
    Ping: no
    SSH: closed/unreachable

  192.168.87.243
    Same subnet: no
    Ping: yes
    SSH: open
    Route: via 192.168.0.1

Recommendation:
  ssh gagneet@192.168.87.243
```

## Then add UI

After the console app is reliable, add:

* WPF if Windows-only.
* Avalonia if cross-platform.

For your current need, WPF is fine.

## Then add discovery

Add:

```text
Scan 192.168.0.0/24
Scan 192.168.87.0/24
Detect SSH hosts
Detect web admin pages
Detect mDNS names
Detect SSDP devices
```

## Then add passive capture

Only after this:

```text
Npcap
SharpPcap
PacketDotNet
ARP/DNS/DHCP/mDNS/SSDP observer
```

---

# What I would name the first feature

**“Where is my server?”**

It should do exactly this:

```text
Find my known server across my home network, tell me the current IP, tell me whether SSH is open, and give me the exact SSH command.
```

That is much more valuable to you immediately than starting with full packet sniffing.

---

# Final answer for your current state

Based on what you provided:

```text
Your PC is currently on:
192.168.0.75

Your server is currently on:
192.168.87.243

ARP does not show the server because it is not on your local 192.168.0.0/24 subnet.

SSH works because traffic is being routed between 192.168.0.0/24 and 192.168.87.0/24.

Use:
ssh <username>@192.168.87.243
```

To confirm the path:

```powershell
tracert 192.168.87.243
```

To confirm SSH:

```powershell
Test-NetConnection 192.168.87.243 -Port 22
```

The application should therefore be a **route-aware server locator and network discovery utility**, not just an ARP scanner or packet sniffer.


