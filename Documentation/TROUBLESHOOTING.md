# Troubleshooting

Logs live in `%APPDATA%\PCRemoteCenter\logs` (`server-YYYYMMDD.log`, JSON lines, and `crash.log`). The audit trail is in
the app under *Activity Logs*.

## The phone cannot find or reach the PC
| Symptom | Fix |
|---|---|
| Discovery list is empty | Phone and PC must be on the same subnet; turn off "AP/client isolation" on the router; make sure the server is **Running** and *Answer LAN discovery requests* is on; create the firewall rule (*Network Settings*). |
| "Connection timed out" when adding manually | Wrong IP (check *Network Settings → This PC*), Windows network profile is *Public* (switch to *Private*) or no firewall rule yet. |
| "Certificate does not match" | The PC created a new certificate (reinstall, *Create new certificate*, deleted data folder). Pair the phone again. |
| "Authentication failed" | The device was revoked or removed on the PC, or the data folder was reset. Remove the PC on the phone and pair again. |
| "Too many failed attempts" | 5 wrong codes/tokens from your IP in 5 minutes. Wait, then try again. |
| Refused / timeout through Tailscale | On the PC tick *Network Settings → Allow connections through Tailscale* → Apply; make sure Tailscale is connected on both devices, and that a Tailscale ACL or Windows Firewall does not block TCP 47821. The PC page must list the adapter ("Tailscale: 100.x.y.z"). |
| Refused / timeout through WireGuard | Tick *Allow connections through WireGuard*; if the tunnel address is a /32 add the peer subnet to *Extra allowed networks*; allow TCP 47821 in Windows Firewall for the tunnel's network profile. |
| Phone paired at home does not know the VPN address | Connect once on the LAN after enabling the VPN option (the app learns the address), or add the address under *Settings → Add address*. |
| 403 / refused from another VPN (OpenVPN, ZeroTier …) | Add that VPN's subnet to *Network Settings → Extra allowed networks*. |

## Server
| Symptom | Fix |
|---|---|
| "TCP port 47821 is already in use" | Another program uses the port. Pick another one in *Network Settings* and save (the phone then needs the new port; QR pairing carries it automatically). |
| The window disappears | It was minimised to the tray (clock area → PC Remote Center icon). *Security & Settings → General* controls this. The server keeps running. |
| Tray icon missing | Windows may hide it in the overflow (^) menu. |
| App does not start twice | Only one instance is allowed; starting it again just brings the running one to the front. |

## Hardware monitoring
| Symptom | Fix |
|---|---|
| CPU temperature shows *n/a* | Temperature sensors need a kernel driver, which requires **running RemoteControlServer.exe as administrator** (right-click → Run as administrator). The server never elevates by itself. |
| Disk temperature *n/a* | Same, and not all drives expose SMART temperature. |
| GPU data missing | Supported: NVIDIA, AMD, Intel. Update the graphics driver. Integrated Intel GPUs report load but often no dedicated VRAM. |
| Values look frozen | Sampling only runs while a phone is subscribed or the dashboard window is visible. |

## Terminals
| Symptom | Fix |
|---|---|
| Terminal window not visible | *Terminal Sessions → Show window*; enable *Show the terminal windows on this PC* in settings. Closing the window only hides it. |
| "The shell is not running" | The shell exited (e.g. you typed `exit`). Use *Restart* on the phone or on the PC page. |
| Output looks garbled after a very wide command | Use the phone's *fit width* button or resize the PC window; both resize the same ConPTY. |
| Commands asking for admin rights fail | The shells run as your normal user. Start the server as administrator *yourself* if you really need elevated shells. |

## Power actions
* "A shutdown is already scheduled" – cancel it first (*Cancel action* on the phone, or `shutdown /a`).
* **Sleep** puts the PC to sleep; if Hibernate is enabled Windows may hibernate instead (`powercfg /h off` to disable).
* **Wake-on-LAN** does nothing? See `NETWORKING.md` – BIOS and adapter settings must allow it, and wireless adapters rarely support it.

## Shortcuts
* "Only .exe, .msc, .cpl, .lnk files can be launched" – scripts are intentionally blocked; create a `.lnk` or launch scripts through the terminal.
* "Elevated launching is disabled" – enable it explicitly in *Security & Settings* (Windows still prompts with UAC on the PC).

## Uninstall / reset
*Settings → Apps → PC Remote Center → Uninstall* (asks whether to delete settings and paired devices). To reset manually,
close the app and delete `%APPDATA%\PCRemoteCenter`; every phone must pair again.
