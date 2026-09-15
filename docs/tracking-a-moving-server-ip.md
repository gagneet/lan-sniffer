# Tracking a Server Whose IP Keeps Changing

This is the problem LanInspector's **device locator** was built for: the home server
(`ubuntu-svr`) sits behind a chain of routers, and every time the modems and routers are power
cycled it comes back on a different DHCP lease. The address written in `known-devices.json` is
stale within a day, so every tool that trusted that address started reporting the server as
unreachable when it was in fact fine.

## The network

A traceroute settles the shape of it:

```text
> tracert bing.com
  1     4 ms  192.168.87.1      <- this machine's gateway
  2     5 ms  192.168.4.1       <- a second router above it
  3    13 ms  100.96.16.1       <- CGNAT, the ISP side
  4    11 ms  150.171.27.10
```

`192.168.87.1` and `192.168.4.1` both appear, **in the same trace**. They are not one router whose
address changes across reboots — they are two routers, one behind the other, and which one answers
depends on which you ask. Counting the ISP's CGNAT layer, outbound traffic crosses three NATs:

```text
NBN / Internet
  └── CGNAT 100.96.16.1                      (ISP — inbound connections cannot cross this)
        └── router  192.168.4.1
              └── router  192.168.87.1       serves 192.168.87.0/24
                    ├── this machine                        192.168.87.x
                    ├── gagneets-mac-mini  en1 (Wi-Fi)      192.168.87.118
                    └── FAST5366LTE-A      192.168.0.1      serves 192.168.0.0/24
                          └── 6-port unmanaged LAN switch
                                ├── ubuntu-svr         enp2s0  192.168.0.148  68:1d:ef:3c:d5:45
                                └── gagneets-mac-mini  en0     192.168.0.154  1c:f6:4c:51:76:d3
```

The FAST5366LTE-A is **below** the mesh, not above it, and is not in the path to the internet at
all — which is why querying `192.168.0.1` says nothing about the traffic leaving the house.

A traceroute from `ubuntu-svr` itself settles which one it hangs off: `192.168.0.1`, then
`192.168.87.1`, then `192.168.4.1`. The FAST5366LTE-A's parent is the Google Nest on `192.168.87.1`,
which in turn hangs off the Eero on `192.168.4.1`. `laninspector locate` runs that trace for you on
any device it can log in to — see [When the server is behind another router](#when-the-server-is-behind-another-router).

### Why `ubuntu-svr` is unreachable from here and the Mac Mini is not

The client is on `192.168.87.0/24`. `ubuntu-svr` is on `192.168.0.0/24`, one NAT layer *further
down*, behind the FAST5366LTE-A — and a NAT router does not carry inbound connections from its WAN
side to its LAN. The Mac Mini is reachable only because its Wi-Fi interface sits on
`192.168.87.0/24` alongside the client; its `192.168.0.154` address is just as unreachable as the
server's.

So this is not a server fault and not a fixable routing accident: it is what the topology does.

The Mac Mini is **dual-homed**: wired into the switch on `192.168.0.0/24` *and* on Wi-Fi to the
Nest on `192.168.87.0/24`. It is the one machine sitting on both sides of the boundary that makes
SSH fail from the Nest side, which makes it the natural place to run a Tailscale subnet router.

`ubuntu-svr` also reports a set of `10.20.x.1` addresses (`docker0`, `docker_gwbridge`, four
`br-*` bridges) and a pile of `veth*` and `cali*` interfaces. Those are Docker and Kubernetes
container networks. They are real on the server and completely unreachable from any other
machine — see [Container addresses](#container-addresses-on-ubuntu-svr) below for why that matters
to the locator.

Two separate things move here, and it helps to keep them apart:

| What moves | Why | What fixes it |
|---|---|---|
| `ubuntu-svr`'s address inside `192.168.0.0/24` | The FAST5366LTE-A hands out a new lease after a power cycle | A DHCP reservation on the FAST5366LTE-A, keyed to `68:1d:ef:3c:d5:45` |
| The Mac Mini's `en0` address | Same DHCP server, same cause | A reservation keyed to `1c:f6:4c:51:76:d3` |
| The Mac Mini's `en1` address | The Nest's DHCP, plus macOS private Wi-Fi addressing | Turn off "Private Wi-Fi Address" for that network if you want a stable reservation — otherwise track it by hostname |
| Which router address you see (`192.168.87.1` vs `192.168.4.1`) | Nothing moves — these are two routers stacked one behind the other, so the answer depends on which one you queried | Nothing to fix; see the traceroute above |

Note the Mac Mini's `en1` MAC (`86:61:7a:c6:1f:7f`) has the locally-administered bit set — it is a
randomised Wi-Fi address, not the hardware one. Only `en0`'s MAC is worth putting in
`knownMacs`; a randomised address will change and silently stop matching.

The unmanaged switch is invisible to all of this — it does not assign addresses and cannot be
queried. Everything below works at the layer above it.

## The stable answer: Tailscale

`ubuntu-svr` is already on the tailnet:

```text
> ping ubuntu-svr
Pinging ubuntu-svr.tail7f7c1e.ts.net. [100.83.183.74] with 32 bytes of data:
Reply from 100.83.183.74: bytes=32 time=2ms TTL=64
```

`100.83.183.74` is assigned by the tailnet, not by any router here, so it does not change when the
DHCP lease does. **For reaching the server, prefer the Tailscale name over any LAN address** — it
works from the LAN, from the Nest side, and from outside the house:

```bash
ssh gagneet@ubuntu-svr
```

LanInspector now puts the MagicDNS name first when it generates an SSH command for a device with a
matching online Tailscale peer, precisely so that a copied command survives the next reboot.

## Finding the current LAN address

The Tailscale address does not tell you the LAN address, and the LAN address still matters: for
services bound to the LAN interface, for the router's admin pages, and for understanding what the
network is actually doing.

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
  Candidates:
    192.168.0.154    [open :22]     ARP cache on enp3s0: 9C:6B:00:AA:BB:CC -> 192.168.0.154, state REACHABLE
    192.168.0.148    [not probed]   Configured in known-devices.json as 192.168.0.148
  Evidence:
    * ARP cache maps 9C:6B:00:AA:BB:CC to 192.168.0.154, state REACHABLE.
    * TCP port 22 accepted a connection at 192.168.0.154.
```

`laninspector locate` with no device id locates every known device. `--json` gives machine-readable
output for scripting; `--probe` additionally runs `tailscale ping`.

In the WPF app the same information appears under **Critical devices**: the current address, a
"found via" line naming the evidence, the Tailscale address, and an amber note when the address has
changed since the last check. **Copy IP** puts the located address on the clipboard, and the SSH
buttons build their command from the address that was actually found.

## How the locator decides

Evidence is gathered from every source that is cheap enough to consult, ranked, and then probed.
The first candidate that accepts a TCP connection on the device's SSH port wins. If nothing
answers, the strongest unverified candidate is reported and flagged as unconfirmed, so you get a
best guess with its provenance rather than a blank.

| Rank | Source | What it proves | When it is unavailable |
|---|---|---|---|
| 1 | **ARP cache**, matched by MAC (after a subnet sweep if the MAC is missing) | The device is on this layer-2 segment right now, and the MAC identifies it beyond doubt | The device is behind another router |
| 2 | **The device itself**, asked over SSH | The device holds this address | No `ssh` profile, no key loaded, or SSH is off |
| 3 | **Tailscale direct path** (`CurAddr`) | Tailscale is sending packets to this address right now — which may be a NAT router in front of the device | Traffic is relayed through DERP |
| 4 | **`tailscale ping`** (`--probe` only) | A direct path was just established to this address | The peers cannot reach each other directly |
| 5 | **Tailscale endpoint list** (`Addrs`, `PeerAPIURL`) | Tailscale believes the peer can be reached here | The peer has not advertised endpoints |
| 6 | **Hostname lookup** (`ubuntu-svr`, `ubuntu-svr.local`) | Something answering to that name is at this address | No DNS registration and no mDNS responder |
| 7 | **Remembered address** | Where the device was last found | First run |
| 8 | **Configured address** | Only what someone typed into the config | — |

Addresses found by several sources are probed and reported once, attributed to their strongest
evidence.

## Set it up properly

**1. Add the server's MAC.** This is the single highest-value change — it moves the server to rank 1
and makes the answer certain instead of inferred. On the server:

```bash
ip link show          # the "link/ether" value for the LAN interface
```

Then in `known-devices.local.json`:

```json
{
  "knownDevices": [
    {
      "id": "home-server",
      "knownMacs": ["9c:6b:00:aa:bb:cc"],
      "knownHostnames": ["ubuntu-svr"],
      "knownTailscaleNames": ["ubuntu-svr"]
    }
  ]
}
```

Only the fields you want to change need to be present — files are merged by device id.

**2. Reserve the lease on the FAST5366LTE-A.** A DHCP reservation keyed to that same MAC stops the
address moving in the first place. This is the real fix; the locator is what copes until it is done,
and what copes with the Nest side, where you do not control the DHCP server.

**3. Advertise the LAN subnet through Tailscale**, so clients on the Nest side can reach the whole
`192.168.0.0/24` and not just the server. On `ubuntu-svr`:

```bash
sudo tailscale set --advertise-routes=192.168.0.0/24
```

`ubuntu-svr` already advertises this route; it is waiting to be approved (step 4). Use `tailscale set`
rather than `tailscale up`: given one flag, `up` refuses to run unless every other non-default
setting is restated too. On Linux the subnet router also needs IP forwarding enabled.

The Mac Mini is the better subnet router if you want *both* sides reachable, because it is the only
machine on both — but only if it is on Tailscale. The `ifconfig` output above shows no `utun`
interface carrying a `100.x` address, so Tailscale does not appear to be running on it yet:

```bash
# on gagneets-mac-mini, once Tailscale is installed
sudo tailscale set --advertise-routes=192.168.0.0/24,192.168.87.0/24
```

**4. Approve the route** in the Tailscale admin console: Machines → the device → Edit route
settings. An advertised route does nothing until it is approved, and `laninspector locate` flags one
that is waiting. `laninspector tailscale routes` prints the
command for each device tagged `server`.

## Why SSH works from `192.168.0.x` but not from the Nest side

`laninspector visibility` explains this per device, and it is worth stating plainly: the
FAST5366LTE-A is a NAT router, and a NAT router lets nothing in from its WAN side (the Nest's
`192.168.87.0/24`) to its LAN unless a port is forwarded. Nothing on the server can fix that; the
traffic has to bypass the NAT via Tailscale. That is what steps 3 and 4 above are for.

## Container addresses on `ubuntu-svr`

Tailscale advertises *every* address a peer has as a candidate endpoint. On a host running Docker
and Kubernetes that includes the container bridges:

```text
docker0          10.20.0.1
docker_gwbridge  10.20.1.1
br-60fc61f72810  10.20.2.1
br-88c29693c4a9  10.20.3.1
br-be4c02214168  10.20.4.1
br-790344c95f0e  10.20.5.1
```

These are RFC1918 addresses and look exactly like LAN addresses to a naive filter — but they exist
only inside the server. Reporting `10.20.4.1` as "the server's current IP" would be worse than
useless.

The locator ranks every candidate by plausibility *before* it ranks by evidence strength:

| Tier | Meaning |
|---|---|
| `OnLocalSubnet` | Inside one of this machine's own interface subnets — directly reachable |
| `ConfiguredForDevice` | Inside a subnet or `/24` this device is configured for — plausibly routed |
| `Unrelated` | On no subnet either machine is known to use — almost certainly a bridge on the peer |

A container bridge lands in `Unrelated` and is demoted below even a stale configured address, and
the evidence line says why. The TCP probe then settles it regardless: `10.20.4.1` will not answer
from here, and an address that does answer wins outright.

This is also why `knownSubnets` is worth filling in. It is what tells the locator that
`192.168.87.118` is a legitimate second address for the Mac Mini rather than noise.

## Devices with more than one interface

`laninspector locate` probes every candidate rather than stopping at the first that answers,
because a dual-homed machine genuinely has more than one current address:

```text
Mac Mini (gagneets-mac-mini) (mac-mini)
  Current LAN IP : 192.168.0.154
  Found via      : ARP cache, matched by MAC
  Confidence     : Confirmed
  Also at        : 192.168.87.118  (more than one active interface)
```

Which one you use depends on where *you* are. From the `192.168.0.x` side, `.154` is a direct
layer-2 hop; from the Nest side, `192.168.87.118` is, and `.154` is unreachable.

## Devices with no open ports

A Mac with Remote Login switched off has no port to connect to, so a TCP-only probe would report
it as unconfirmed even while it sits there answering pings. When no probed port responds, the
locator falls back to an ICMP echo; a device verified that way is reported at `High` confidence
rather than `Confirmed`, and the candidate is marked `[ping only]` — present, but no service
proven.

## "Not reachable" when the server is plainly running

A device can be up, busy and perfectly healthy and still show as unreachable, because reachability
is a property of the network *between* the two machines, not of either one.

The giveaway is which devices work. On this network the Mac Mini reports online while `ubuntu-svr`
does not, and the only difference between them is that the Mac Mini has a second interface on
`192.168.87.0/24`:

```text
ubuntu-svr           enp2s0  192.168.0.148    (1.2 GB in, 653 MB out — plainly alive)
gagneets-mac-mini    en0     192.168.0.154
                     en1     192.168.87.118   <-- the reason it is reachable
```

If the machine running LanInspector is on `192.168.87.x`, then it shares a subnet with the Mac
Mini's Wi-Fi interface and can reach it directly — while `192.168.0.148` sits behind the
FAST5366LTE-A, whose NAT lets nothing in from the Nest side. Nothing is wrong with the server; there is simply
no path from that side of the house to that subnet. Tailscale still works because it does not use
that path at all.

The critical-devices panel now says so rather than leaving "Not reachable" to be interpreted. The
row itself carries only the short cause, because a row has space for two or three words and a
paragraph pressed into it would push the address out of view — which is the problem this layout was
rebuilt to fix:

```text
Device                LAN address       Tailscale          Status
Home Server           192.168.0.148     100.83.183.74      [ Not reachable ]   v
  ARP cache, by MAC   Low               reachable this way   different subnet
```

Clicking the chevron opens the full account underneath, and only one row opens at a time:

```text
This machine is on 192.168.87.0/24; 192.168.0.148 is on 192.168.0.0/24. Those are different
subnets, and the router between them does not carry traffic from this side to that one.
Tailscale reaches it now at ubuntu-svr.

Connect to the same network as the target, add a route, or reach it over Tailscale.
```

The CLI has no room for structure, so `locate` prints the same thing on one line:

```text
  Current LAN IP : 192.168.0.148
  Confidence     : Low
  Why            : different subnet - This machine is on 192.168.87.0/24; 192.168.0.148 is on
                   192.168.0.0/24. ... Connect to the same network as the target, add a route,
                   or reach it over Tailscale.
```

When the target *is* on the same subnet and still does not answer, the message says the opposite —
that the route is fine and the service or a host firewall is the thing to look at. The two cases
need opposite responses, so the app names which one it is.

The SSH command follows the same logic: when the LAN address is unconfirmed, the button offers the
Tailscale name instead, because a command that works beats one that matches the config.

### Confirming it

```bash
laninspector visibility 192.168.0.148    # explains the path in full
laninspector locate home-server          # shows every candidate and what answered
```

## When the server is behind another router

Everything this machine can observe about a device behind a NAT router describes the router.
Tailscale's direct path to `ubuntu-svr` from the Nest side ends at the FAST5366LTE-A's WAN address,
so earlier versions reported that `192.168.87.x` address as the server's LAN IP.

`locate` now catches this in two ways:

- **Without logging in.** When the ARP cache holds that address under a MAC that is not in the
  device's `knownMacs`, the address belongs to a router in front of the device. It is shown as
  `Reached through`, not as the device's address. A randomised MAC proves nothing, so it is ignored.
- **By asking the device.** For a device with an `ssh` profile, `locate` logs in with keys only
  (`BatchMode`: never a password prompt), over its Tailscale address when it is online there. It
  runs `ip`/`ifconfig`, `route` and a four-hop `traceroute`. The device's own addresses replace the
  guesses, and its gateway and the routers beyond it are shown:

```text
  Current LAN IP : 192.168.0.148
  Found via      : reported by the device over SSH
  Connected via  : enp2s0 192.168.0.148/24 -> 192.168.0.1 -> 192.168.87.1 -> 192.168.4.1
  Gateway MAC    : 44:AD:B1:D6:39:59
  Reached through: 192.168.87.23  (a router in front of it, not the device)
  Tailscale route: 192.168.0.0/24 is advertised but NOT approved
  Why            : behind another router - ubuntu-svr is at 192.168.0.148 on 192.168.0.0/24, behind 192.168.0.1. ...
```

The first router in that chain that is on this machine's network is where the two branches meet;
every router before it stands in between. Unmanaged switches never appear: they have no address and
take no part in routing.

`--no-ssh` skips the login. A Tailscale address's host key is accepted on first use, because only
the peer holding its WireGuard key can answer there. A LAN address needs its key in `known_hosts`
already. The WPF app does the same for every critical device with an `ssh` profile, and shows the
chain under **Connected via** when the row is expanded.

If the diagnosis is right, the fix is one of:

- **approve the route** the server already advertises: Tailscale admin console → Machines →
  `ubuntu-svr` → Edit route settings → enable `192.168.0.0/24`. Linux clients also need
  `sudo tailscale set --accept-routes`. Windows and macOS clients accept routes by default;
- run LanInspector from a machine on `192.168.0.0/24`;
- or forward port 22 on the FAST5366LTE-A to the server's reserved address.

The first option keeps working from outside the house too.

## When SSH is off, or the address has changed

Finding a device's address needs nothing running on it. It needs the device's MAC, recorded once:

1. **Write the MAC down while you have access.** Run `ip link` (Linux), `ifconfig en0 | grep ether`
   (macOS) or `getmac /v` (Windows) on the device. You can also read it from the router's DHCP client
   list, or from the label on the device. Put the wired interface's MAC in `knownMacs`, not a
   randomised Wi-Fi one.
2. **Let `locate` sweep.** A router or switch restart hands out new leases and empties ARP caches.
   When the MAC is missing from the cache, `locate` pings every address on this machine's own
   subnets and reads the cache again. The operating system has to resolve each address's MAC before
   it can send the ping, so the device turns up even if it ignores pings. `--no-sweep` skips this,
   and subnets larger than /22 are never swept.

The sweep only reaches devices on a subnet this machine is on, because ARP does not cross routers.
For a device behind another router, in order of preference:

- **Tailscale on the device.** Its `100.x` address and MagicDNS name never change, whatever the
  routers do. `sudo tailscale set --ssh` also provides SSH over Tailscale with no SSH server installed.
- **A DHCP reservation** on the router the device hangs off, keyed to its MAC, so the address stops
  moving.
- **mDNS.** Ubuntu with `avahi-daemon`, and every Mac, answer to `hostname.local` on their own
  segment, and `locate` tries that name.
- **Physical access.** Plug a laptop into the same switch, or run `laninspector locate` on a machine
  already on that segment. The dual-homed Mac Mini is one.

To turn remote access on while you are at the machine: `sudo apt install openssh-server` (Ubuntu),
System Settings → General → Sharing → Remote Login (macOS), or the OpenSSH Server optional feature
(Windows). Avoid Telnet: it sends passwords in clear text, and SSH is available everywhere Telnet is.

## Keeping a log of the moves

Each `locate` records where the device was found, in:

- **Linux/macOS**: `~/.config/laninspector/device-locations.json`
- **Windows**: `%APPDATA%\LanInspector\device-locations.json`

The file holds the current address, the previous one, and when the change was first seen — which is
what produces the "Changed: was 192.168.0.148" line. To build a history, run `locate` on a schedule:

```bash
# crontab -e  — check every 15 minutes and append changes to a log
*/15 * * * * /usr/local/bin/laninspector locate home-server --json >> ~/laninspector-locate.log
```
