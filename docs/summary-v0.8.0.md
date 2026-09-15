The server's IP is 192.168.0.148, and your laptop can't reach it because the server sits behind the Optus router, whose NAT blocks connections coming in from your side. The code is fixed, and one step in the Tailscale admin console will make that address reachable.



Everything builds, including the WPF app, and all 292 unit tests pass. I haven't run the app or the network commands; please test those on Windows. Nothing is committed.



Your network



The server's own traceroute shows the chain:



ubuntu-svr enp2s0 192.168.0.148 → Optus 192.168.0.1 → Nest 192.168.87.1 → Eero 192.168.4.1



\- Why it fails: your laptop is on the Nest network (192.168.87.x). The server hangs off the Optus router, which doesn't let connections in from the Nest side. The unmanaged switch doesn't show up at all, because it has no IP address.

\- Tailscale addresses: 100.66.86.87 is your laptop. The server's Tailscale IP is 100.83.183.74, and ssh gagneet@ubuntu-svr works from anywhere.

\- The one fix to make now: the server already offers 192.168.0.0/24 to Tailscale, but nobody has approved it. In the admin console go to Machines → ubuntu-svr → Edit route settings and enable it. After that, 192.168.0.148 and the Mac's 192.168.0.154 work from any device on Tailscale.

\- The bug: the app used to report the Optus router's outside address as the server's IP.



What changed



\- Detecting the router in front:

&#x20; - Without logging in: if the address Tailscale connects to has a MAC that isn't the device's, the app now labels it "Reached through (router)" instead of calling it the device.

&#x20; - By logging in: for devices with an SSH profile, it logs in with keys only (never a password) and reads the device's own addresses, gateway, the routers beyond, and any unapproved Tailscale route. It prefers the Tailscale address and doesn't accept an unknown host key on a LAN address.

\- Diagnosis: a new "behind another router" explanation names the routers and gives the fix in order: approve the route, use Tailscale meanwhile, or forward a port.

\- CLI: locate gains "Connected via", "Gateway MAC", "Reached through" and a warning for unapproved routes, plus the same fields in --json. New flags --no-ssh and --no-sweep turn the new steps off.

\- WPF app: expanding a critical device now shows "Connected via". The SSH button only uses a LAN address once a connection to it has actually worked; otherwise it uses the Tailscale name.

\- Fixes:

&#x20; - Route suggestions now say tailscale set instead of tailscale up, which refuses to run with a single flag.

&#x20; - Three tests were resolving "ubuntu-svr" through real DNS on this machine.

&#x20; - The Eero/Nest labels in the docs were swapped.



If SSH is off, or the IP changed after a restart



Finding a device's IP doesn't need SSH or anything else running on it. It needs the device's MAC address, recorded once:

1\. Record it: while you have access, run ip link on Linux, ifconfig en0 on macOS or getmac /v on Windows. You can also read it from the router's device list or the label. Add it to knownMacs, using the wired interface; a randomised Wi-Fi MAC changes.

2\. Sweep: this is new. When that MAC isn't in the ARP cache (the table of IP-to-MAC pairs your machine has recently seen), locate and the app ping every address on your own network and look again. The device shows up by its MAC even if it ignores pings.



This only works on a network your machine is directly on. For devices behind another router:

\- Tailscale on the device: its address never changes, and sudo tailscale set --ssh gives you SSH without an SSH server installed.

\- DHCP reservation: reserve the device's address on the router it's connected to, so it stops changing.

\- hostname.local: Macs, and Ubuntu with avahi installed, answer to this on their own network, and the app tries it.

\- Physical access: plug into the same switch. The Mac mini is already on both networks, so it can run locate for you.



To switch on remote access while you're at a machine:

\- Ubuntu: sudo apt install openssh-server

\- macOS: Settings → Sharing → Remote Login

\- Windows: the OpenSSH Server optional feature



I'd avoid Telnet: it sends passwords in plain text, and SSH is available everywhere Telnet is.



As for confirming all devices: sweeping by MAC covers every device on a network you're directly connected to. Anything behind another router needs Tailscale on it, or a machine on that network running the app.



To test on Windows



1\. Run laninspector locate home-server. You should see "Reached through" with an 87.x address and the "NOT approved" route line. The server's key has to be loaded in your ssh-agent, or the login step reports "did not accept an SSH key".

2\. Approve the route and run it again.

3\. Open the Critical devices panel and expand ubuntu-svr.



I also saved your network layout to memory and added a CLAUDE.md for future sessions.

