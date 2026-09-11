# Flattening a Multi-Router Home Network

This network stacks four routing layers between a device and the internet. That is the single
cause behind most of the symptoms in these docs: a server that cannot be reached, addresses that
appear to change, no device that can see the whole network, and nothing that reports SNMP.

## What is there now

```text
NBN / Internet
  └── CGNAT 100.96.16.1                    (the ISP's own NAT — inbound cannot cross it)
        └── router  192.168.4.1            NAT #1
              └── router  192.168.87.1     NAT #2   serves 192.168.87.0/24
                    ├── Windows client, Mac Mini (Wi-Fi)
                    └── FAST5366LTE-A  192.168.0.1   NAT #3   serves 192.168.0.0/24
                          └── 6-port unmanaged switch
                                ├── ubuntu-svr        192.168.0.148
                                └── Mac Mini  en0     192.168.0.154
```

Each NAT layer is a one-way door: connections pass outward and are refused inward. So

- a client on `192.168.87.x` cannot open a connection to `192.168.0.148`, two doors down;
- mDNS/Bonjour does not cross subnets, so AirPlay, Chromecast and printer discovery only work
  within one segment;
- port forwarding would have to be configured identically on three routers *and* still die at the
  ISP's CGNAT;
- no single device carries everyone's traffic, so nothing can measure or monitor it;
- the Mac Mini is dual-homed across two of the segments, which is why it alone appears reachable —
  and why its two addresses behave differently.

## The two options, assessed

### Option A — a switch on the NBN router with all three routers hanging off it

This removes the *chain* but not the *problem*. Three routers side by side, each doing NAT, gives
three isolated subnets instead of three nested ones. A device on the Eero still cannot reach a
device on the FAST5366; it is the same wall, rearranged.

It only helps if those routers stop being routers — which is Option B, and then the switch is just
convenient cabling rather than the fix.

### Option B — one router, everything else as access points ✅

**This is the one to do.** One device does DHCP and NAT; every other box becomes a dumb access
point or switch on the same subnet.

```text
NBN / Internet
  └── CGNAT (ISP)
        └── one router          192.168.1.1   the only DHCP server and NAT
              ├── Eero / Nest   access point, no DHCP, no NAT
              ├── FAST5366LTE-A access point, no DHCP, no NAT
              └── 6-port switch
                    ├── ubuntu-svr
                    └── Mac Mini
```

Everything lands in one subnet. `ubuntu-svr` becomes reachable from every machine in the house,
mDNS works everywhere, there is one DHCP server to hold reservations, and one device sees all
traffic.

## Choosing which router keeps the job

| Candidate | For | Against |
|---|---|---|
| **Eero** | Best Wi-Fi coverage; mesh works properly when it owns the network | No SNMP, ever — monitoring stays limited to what a client can see |
| **FAST5366LTE-A** | Sagemcom units often expose SNMP; may offer LTE failover | Weaker Wi-Fi than a mesh; ISP firmware |
| **NBN-supplied router** | Fewest devices in the path | Usually the most limited firmware |

If Wi-Fi coverage matters most, keep the Eero as the router. If monitoring matters most, keep the
FAST5366 and run the Eero as a mesh of access points — then re-run `laninspector snmp discover`,
because a Sagemcom in charge is the one candidate here that might answer.

You cannot have both from this hardware. A managed switch would give you monitoring regardless of
which router wins, and is the better purchase if you want per-device traffic detail.

## Turning a router into an access point

Most consumer routers have an "Access Point" or "Bridge" mode in their admin pages. Where one is
missing, the same result comes from three steps:

1. Connect it **LAN port → LAN port** to the main router. Leave its WAN/Internet port empty — using
   it is what creates the extra NAT layer.
2. **Disable its DHCP server.**
3. Give it a static address inside the main subnet, outside the DHCP pool.

It then passes traffic through at layer 2 and adds no routing boundary. Its Wi-Fi keeps working;
give it the same SSID and password as the main router for seamless roaming, or a distinct one if
you would rather choose between them.

For the Eero specifically, bridge mode is a setting in the eero app — note that it disables eero's
own DHCP, guest network and eero Secure features, since those need it to be the router.

## What this does not fix

**CGNAT still blocks all inbound connections from the internet.** `100.96.16.1` is the ISP's NAT,
not yours, and no amount of reorganising the house changes it. Reaching anything from outside
still means Tailscale (or an equivalent), which is why `tailscale up --advertise-routes` is worth
keeping regardless of the topology.

Flattening the network makes everything reachable *inside* the house and gives monitoring a place
to stand. It does not open the door from outside — nothing on your side of the CGNAT can.

## Order of work

1. Decide which router keeps DHCP and NAT, from the table above.
2. Put the others into access-point mode, one at a time, checking Wi-Fi still works after each.
3. Move the 6-port switch onto the main router.
4. Set a DHCP reservation for `ubuntu-svr` (`68:1d:ef:3c:d5:45`) and the Mac Mini
   (`1c:f6:4c:51:76:d3`) on the one remaining DHCP server.
5. Re-run `laninspector locate` and `laninspector snmp discover`. Both should look quite different.
