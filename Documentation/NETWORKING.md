# Networking

## Ports

| Port | Protocol | Purpose | Configurable |
|---|---|---|---|
| **47821** | TCP | TLS WebSocket (`wss://<pc>:47821/ws`) used by the phone | yes (*Network Settings*) |
| **47822** | UDP | LAN discovery (phone broadcasts `PCRC_DISCOVER_V1`, the PC answers with its name, port and certificate fingerprint) | on/off |

Wake-on-LAN magic packets are sent **by the phone** (UDP 9 and 7, broadcast) – the PC does not need to listen for them.

## Default: LAN only

The server accepts connections from loopback and private ranges only: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`,
`169.254.0.0/16` and IPv6 link-local/unique-local. Everything else gets HTTP 403 before TLS data is processed.

## Windows Firewall

Windows blocks incoming connections until a rule exists. In *Network Settings* press **Create firewall rule…**. After you
approve the UAC prompt it adds (private profile, `remoteip=LocalSubnet`, bound to this program):

```
netsh advfirewall firewall add rule name="PC Remote Center" dir=in action=allow protocol=TCP localport=47821 profile=private remoteip=LocalSubnet program="<path>\RemoteControlServer.exe"
netsh advfirewall firewall add rule name="PC Remote Center" dir=in action=allow protocol=UDP localport=47822 profile=private remoteip=LocalSubnet program="<path>\RemoteControlServer.exe"
```

Make sure your Wi-Fi/Ethernet is classified as a **Private** network (Settings → Network & internet → Properties).
*Remove firewall rule…* deletes them again.

## Connecting

1. **QR code (recommended)** – *Connected Devices → Generate pairing code*, scan the QR in the app. The QR carries the PC's
   addresses, port, one-time code and certificate fingerprint.
2. **Discovery** – the app lists PCs that answer on UDP 47822. Select one, compare the shown fingerprint with the PC
   (*Connected Devices* page) and enter the pairing code.
3. **Manual** – enter IP address or host name and port, then the pairing code and confirm the fingerprint.

Discovery uses UDP broadcast and works on the same subnet only. Some routers isolate Wi-Fi clients ("AP isolation" /
"guest network") – then neither discovery nor connections work; use a normal LAN SSID.

## Remote access (outside your home): Tailscale and WireGuard

Do **not** forward port 47821 on your router. Use a VPN so the phone gets a private path to the PC. PC Remote Center
**recognises Tailscale and WireGuard on the PC, shows their addresses, and can accept connections that arrive through
them**. It does not install, start or configure the VPN itself – you set that up with the official Tailscale / WireGuard apps.

### Tailscale (easiest)
1. Install Tailscale on the PC and on the phone and sign in to the **same tailnet**.
2. PC: *Network Settings → Remote access: Tailscale & WireGuard* → the Tailscale adapter and its `100.x.y.z` address appear.
   Tick **Allow connections through Tailscale** → *Apply* (confirm the prompt). No restart needed.
3. Pair: *Connected Devices → Generate pairing code*. The QR now also carries the Tailscale address, so a phone paired at home
   can later connect from anywhere. (A phone already paired earlier learns the new address automatically the next time it connects over the LAN.)
4. Away from home, with Tailscale on, the app tries its saved addresses in order and shows a **Tailscale** badge in the header.
   You can add the PC's MagicDNS name (e.g. `my-pc.tail1234.ts.net`) in the app under *Settings → Add address*.

Notes: the whole tailnet range `100.64.0.0/10` is accepted, so use **Tailscale ACLs** to restrict which of your devices can reach
this PC's port 47821. After enabling a VPN option press **Create firewall rule…** again: it then also adds a TCP rule limited to exactly
the VPN source range(s) you allowed (on every network profile, because VPN adapters are often classified "Public").

### WireGuard
1. Run a WireGuard tunnel on the PC (WireGuard for Windows) **and** on the phone, with the PC reachable at its tunnel address.
2. PC: tick **Allow connections through WireGuard** → *Apply*. The *tunnel's subnet* (address/prefix of the tunnel adapter, e.g.
   `10.8.0.2/24` → `10.8.0.0/24`) is accepted. If the tunnel address is a single `/32`, the PC cannot know the peers' subnet – add it
   under *Extra allowed networks* instead.
3. In the app add the PC's tunnel address under *Settings → Add address* (or pair while the tunnel is up so the QR contains it).

### How the protection works
* The VPN options are **off by default** and need an explicit confirmation.
* A VPN source range is accepted **only if the connection arrives on that VPN adapter's own address**. A packet with a `100.x`
  source that arrives on your Ethernet/Wi-Fi adapter is still refused.
* Pairing codes, device tokens, permissions and certificate pinning apply exactly as on the LAN; the VPN only decides *who can reach the port*.
* Other VPNs (OpenVPN, ZeroTier, …) are listed as "detected" only. For them use *Extra allowed networks* with their subnet.

Keep **LAN only** enabled; the VPN options and the extra ranges are the only additions. Unchecking it accepts *any* source address
and is only appropriate behind a firewall that already blocks the internet.

## Wake-on-LAN

* Enable it in the BIOS/UEFI (names vary: *Wake on LAN*, *Power on by PCI-E*) and in Device Manager → network adapter →
  *Power Management* → "Allow this device to wake the computer" / "Only allow a magic packet…", and *Advanced → Wake on Magic Packet*.
* *Network Settings → Wake-on-LAN readiness* lists adapters with their MAC addresses and whether Windows reports WoL as enabled.
* The phone stores the MAC/broadcast address of each PC when it connects, so it can wake the PC while it is off.
* WoL generally only works for **wired** adapters, and only on the same subnet.

## Heartbeat and reconnection

The phone sends `ping` every 10 s (the PC drops connections idle for 60 s) and reconnects automatically with exponential
back-off (1 s → 30 s). After a server restart or Wi-Fi change it logs in again with its stored token.
