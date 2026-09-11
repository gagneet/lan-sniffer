# Tracking a Server Whose IP Keeps Changing

This is the problem LanInspector's **device locator** was built for: the home server
(`ubuntu-svr`) sits behind a chain of routers, and every time the modems and routers are power
cycled it comes back on a different DHCP lease. The address written in `known-devices.json` is
stale within a day, so every tool that trusted that address started reporting the server as
unreachable when it was in fact fine.

## The network

```text
NBN / Internet
  └── Eero router            192.168.87.1  or  192.168.4.1   (changes across reboots)
        └── FAST5366LTE-A    192.168.0.1                     (Optus modem-router, DHCP server)
              └── 6-port unmanaged LAN switch
                    └── ubuntu-svr        192.168.0.x         (currently .154, was .148)
```

Two separate things move here, and it helps to keep them apart:

| What moves | Why | What fixes it |
|---|---|---|
| `ubuntu-svr`'s address inside `192.168.0.0/24` | The FAST5366LTE-A hands out a new lease after a power cycle | A DHCP reservation on the FAST5366LTE-A, keyed to the server's MAC |
| The Eero's own address (`192.168.87.1` ↔ `192.168.4.1`) | The Eero picks a different private range depending on what it sees upstream at boot | Pin the Eero's LAN subnet in its app, or leave it and let the locator track it |

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
works from the LAN, from the Eero side, and from outside the house:

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
| 1 | **ARP cache**, matched by MAC | The device is on this layer-2 segment right now, and the MAC identifies it beyond doubt | The device is behind another router, or has not spoken recently |
| 2 | **Tailscale direct path** (`CurAddr`) | Tailscale is sending packets to this address right now | Traffic is relayed through DERP |
| 3 | **`tailscale ping`** (`--probe` only) | A direct path was just established to this address | The peers cannot reach each other directly |
| 4 | **Tailscale endpoint list** (`Addrs`, `PeerAPIURL`) | Tailscale believes the peer can be reached here | The peer has not advertised endpoints |
| 5 | **Hostname lookup** (`ubuntu-svr`, `ubuntu-svr.local`) | Something answering to that name is at this address | No DNS registration and no mDNS responder |
| 6 | **Remembered address** | Where the device was last found | First run |
| 7 | **Configured address** | Only what someone typed into the config | — |

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
and what copes with the Eero side, where you do not control the DHCP server.

**3. Advertise the LAN subnet through Tailscale**, so clients on the Eero side can reach the whole
`192.168.0.0/24` and not just the server. On `ubuntu-svr`:

```bash
sudo tailscale up --advertise-routes=192.168.0.0/24
```

Then approve the route in the Tailscale admin console. `laninspector tailscale routes` prints the
command for each device tagged `server`.

## Why SSH works from `192.168.0.x` but not from the Eero side

`laninspector visibility` explains this per device, and it is worth stating plainly: the Eero has
no route to the subnet behind the FAST5366LTE-A. A packet for `192.168.0.154` sent from the Eero
side does not reach the modem's LAN — it goes upstream, usually into CGNAT (`100.64.0.0/10`), where
it dies. Nothing on the server can fix that; the route has to exist on the Eero, or the traffic has
to bypass it via Tailscale. That is what step 3 above is for.

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
