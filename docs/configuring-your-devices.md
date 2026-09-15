# Configuring Your Devices

LanInspector ships with **no devices configured**. The application carries nobody's network in it;
everything below is yours to fill in, and it stays on your machine.

## The Settings tab

The **Settings** tab is the simplest route. It lists every configured device with editable columns,
and **Save** writes to your per-user configuration — never to the copy beside the executable, which
an application update would overwrite.

| Column | What it is for |
|---|---|
| **Critical** | Pins the device to the panel at the top of the window and keeps it checked. |
| **MAC addresses** | The single most useful field. A MAC does not change when the DHCP lease does, so it is how a device is re-found after it moves. |
| **IP addresses** | Starting points only — treated as the weakest evidence, because they go stale. |
| **Subnets** | For a router known by the range it serves, and to tell the locator that an off-subnet address is legitimate for this device rather than noise from a peer's container bridges. |
| **Hostnames** | Used for DNS and mDNS (`<name>.local`) lookups when the device is on another segment. |
| **Tailscale names** | Matches the tailnet peer, which supplies both the stable overlay address and live LAN endpoint evidence. |
| **SSH / user / port** | Enables the SSH buttons and tells the locator which port to probe when verifying an address. With a key loaded in `ssh-agent`, the locator also logs in (keys only, never a password) to ask the device its own addresses, gateway and the routers beyond. That is how a device behind another router is told apart from the router. |
| **Tags** | Free-form. `server` additionally opts a device into `laninspector tailscale routes`. |

Lists accept commas or semicolons: `192.168.0.148, 192.168.0.149`.

## Where configuration lives

Files are **merged by device id, later files winning**:

1. `<exe-dir>/Data/known-devices.json` — shipped defaults, empty
2. `<exe-dir>/Data/known-devices.local.json` — git-ignored, for a portable install
3. `%APPDATA%\LanInspector\known-devices.json` (Windows)
   `~/.config/laninspector/known-devices.json` (Linux/macOS) — **where Save writes**
4. `%APPDATA%\LanInspector\known-devices.local.json`

The CLI reads the same files, plus `./known-devices.json` and `./known-devices.local.json` in the
working directory, which win over everything else.

Because they merge by id, a later file only needs the fields you want to change:

```json
{
  "knownDevices": [
    { "id": "home-server", "knownMacs": ["68:1d:ef:3c:d5:45"] }
  ]
}
```

## Finding a MAC address

| Where | Command |
|---|---|
| Linux | `ip link show` — the `link/ether` value for the LAN interface |
| macOS | `ifconfig en0` — the `ether` value |
| Windows | `ipconfig /all` — "Physical Address", or `getmac /v` |
| The router | Its DHCP client list or connected-devices page |
| From another machine | `laninspector locate <id>` lists what the ARP cache holds |

Any notation works — `aa:bb:cc:dd:ee:ff`, `AA-BB-CC-DD-EE-FF`, or bare hex.

Record it once, while you have access, and the device no longer needs anything running on it to be
found. When its MAC is missing from the ARP cache after a router or switch restart, `locate` pings
every address on this machine's own subnets and reads the cache again. The device turns up by MAC
even with SSH off and every port closed. This only reaches subnets this machine is on; for a device
behind another router, see
[When SSH is off, or the address has changed](tracking-a-moving-server-ip.md#when-ssh-is-off-or-the-address-has-changed).

**Wi-Fi MACs are often randomised.** A MAC whose second hex digit is `2`, `6`, `A` or `E`
(`86:61:7a:…`) has the locally-administered bit set: it is a private address that will change and
silently stop matching. Use the wired interface's MAC, or turn off private addressing for that
network.

## Adding two servers, worked through

`docs/samples/known-devices.two-servers.json` is a complete file for a Linux server and a
dual-homed Mac. Copy it to your user configuration and restart the application, or paste the values
into the Settings tab.

**Windows**

```powershell
$dir = "$env:APPDATA\LanInspector"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item docs\samples\known-devices.two-servers.json "$dir\known-devices.json"
```

**Linux / macOS**

```bash
mkdir -p ~/.config/laninspector
cp docs/samples/known-devices.two-servers.json ~/.config/laninspector/known-devices.json
```

Then check both are seen:

```bash
laninspector locate
```

The fields that matter, and where each value comes from:

| Field | Linux server | Dual-homed Mac |
|---|---|---|
| `knownMacs` | `ip link show` → `link/ether` on the LAN interface | `ifconfig en0` → `ether`. **Only the wired MAC**: Wi-Fi uses a randomised address that changes |
| `knownIps` | The LAN interface's address — ignore `docker0`, `br-*` and `veth*`, which exist only inside the machine | Both interfaces, wired and wireless |
| `knownSubnets` | The subnet the server sits in | **Both** subnets — this is what tells the locator its off-subnet address is legitimate rather than noise |
| `knownHostnames` | `hostname` | `scutil --get LocalHostName` |
| `knownTailscaleNames` | `tailscale status` → the peer's name | Same, if it is on the tailnet |
| `ssh.user` | The account you log in as. Its key must be in `~/.ssh/authorized_keys` for the locator to ask the server how it is connected | Remote Login must be on in Sharing, or probes fall back to ICMP |

## A worked example

`Data/known-devices.example.json` is a template covering a server, a main router and an upstream
router known only by its subnet. Copy it to `known-devices.local.json` and edit, or use it as a
reference while filling in the Settings tab.

## A note on sharing

Device names, MAC addresses, internal IPs and SSH usernames describe your home network. Keep them
in your user configuration rather than committing them: `known-devices.local.json` is git-ignored
for exactly this reason, and the per-user paths above sit outside the repository entirely.
