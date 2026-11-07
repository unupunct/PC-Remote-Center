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

## Remote access (outside your home)

Do **not** forward port 47821 on your router. Use a VPN that gives the phone a private address on your network:

* **WireGuard / OpenVPN** to your router or the PC – add the VPN subnet (e.g. `10.8.0.0/24`) to *Extra allowed networks*.
* **Tailscale / ZeroTier** – add the overlay range (Tailscale: `100.64.0.0/10`) to *Extra allowed networks*, then connect to
  the PC's overlay IP with *Add PC → manual*.

Keep **LAN only** enabled; the extra ranges are the only additions. Unchecking it accepts *any* source address and is only
appropriate behind a firewall that already blocks the internet.

## Wake-on-LAN

* Enable it in the BIOS/UEFI (names vary: *Wake on LAN*, *Power on by PCI-E*) and in Device Manager → network adapter →
  *Power Management* → "Allow this device to wake the computer" / "Only allow a magic packet…", and *Advanced → Wake on Magic Packet*.
* *Network Settings → Wake-on-LAN readiness* lists adapters with their MAC addresses and whether Windows reports WoL as enabled.
* The phone stores the MAC/broadcast address of each PC when it connects, so it can wake the PC while it is off.
* WoL generally only works for **wired** adapters, and only on the same subnet.

## Heartbeat and reconnection

The phone sends `ping` every 10 s (the PC drops connections idle for 60 s) and reconnects automatically with exponential
back-off (1 s → 30 s). After a server restart or Wi-Fi change it logs in again with its stored token.
