# Security model

PC Remote Center lets a phone run commands on your PC, so it is built to be safe by default and to make every
powerful capability an explicit, visible choice.

## What protects you

| Area | How it works |
|---|---|
| **Transport** | TLS 1.2/1.3 only (`wss://`). The server creates a self-signed RSA-3072 certificate on first start. |
| **Server identity** | The phone *pins* the SHA-256 fingerprint of that certificate (delivered by the pairing QR code, or confirmed by you when pairing manually). A different certificate is refused – no CA, no host-name trust. |
| **Pairing** | You press *Generate pairing code* on the PC. The code is 8 random digits, valid for 5 minutes (configurable), usable **once**, and shown with a QR code. Wrong codes count toward a lock-out (5 failures / 5 min per IP). |
| **Device tokens** | After pairing the server issues a 256-bit random token. The PC stores only its **SHA-256 hash**; the phone stores the token encrypted with an Android Keystore key. Tokens are never logged. |
| **Authentication** | The first message must authenticate within 10 s or the connection is closed. Unauthenticated messages are never processed. Sessions expire (default 12 h) and revoked devices are disconnected immediately. |
| **Permissions** | Per device, changeable at any time on the PC: `monitoring` (read-only), `terminal`, `launch`, `power`, `admin`. Every message type checks its permission on the server. |
| **Network exposure** | LAN-only by default (private ranges + loopback). Extra ranges (e.g. a VPN subnet) must be added explicitly. Never forward the port to the internet; use a VPN. |
| **VPN (Tailscale / WireGuard)** | Optional and **off by default**. When the owner enables it, that VPN's source range is accepted only for connections arriving on the VPN adapter's own address (a spoofed `100.x` packet on the LAN adapter is refused). The app never creates or controls a VPN. |
| **Firewall** | A Windows Firewall rule is created **only** when you click *Create firewall rule* and approve the UAC prompt: private networks, local subnet only. |
| **Power actions** | Restart/shutdown/sleep are two-step on the **server**: `power.request` returns a single-use, 30-second, device-bound token; nothing happens until `power.confirm`. Pending shutdowns can be cancelled with `shutdown /a`. |
| **Launching programs** | Only user-approved shortcuts (`.exe`, `.msc`, `.cpl`, `.lnk`, `ms-settings:`) can be started, through structured process APIs – arguments are parsed and re-quoted, no shell is ever involved. Scripts (`.bat`, `.ps1`, …) cannot be launched this way. Editing shortcuts needs `admin`. |
| **Elevation** | The server runs as your normal user and **never elevates itself**. Elevated launches are off by default; when enabled, Windows shows its UAC prompt on the PC for each launch. |
| **Terminal input** | `execute` rejects control characters (no hidden escape sequences); raw input is limited to 8 KiB and available only with the `terminal` permission. |
| **Limits** | 256 KiB max message, 300 messages/s per connection, duplicate `requestId` rejection, input validation on every field. |
| **Audit log** | Pairing, logins, denials, power actions, shortcut changes and terminal use (shell + length only, **never the command text**) go to a SQLite audit log visible in *Activity Logs*. |
| **Notifications** | Windows shows a balloon notification whenever a device connects, with extra emphasis for a newly paired device. |

## What it does *not* protect against

* Anyone who holds a device with the `terminal` permission can run **anything your Windows account can**. Treat the phone like a house key: use a screen lock, revoke it if lost.
* The terminal windows on the PC show everything the phone types and runs.
* A malicious program already running as you could read the server's data folder (`%APPDATA%\PCRemoteCenter`). Secrets there are protected with Windows DPAPI (certificate private key) or stored only as hashes (tokens), but DPAPI does not defend against code running as your user.
* If the PC is on an untrusted network (café Wi-Fi), disable the server or use the firewall; LAN-only mode treats *any* private-range network as trusted.

## Revoking access

*Connected Devices → select device → Revoke access*: the device is disconnected at once and its token stops working.
A new certificate (*Security & Settings → Create new certificate*) invalidates the pin on **every** phone.

## Reporting a vulnerability

Please open a private security advisory on the GitHub repository (Security → Advisories) rather than a public issue.
