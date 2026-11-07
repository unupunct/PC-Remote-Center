# PC Remote Center protocol (version 1)

Transport: **WebSocket over TLS** — `wss://<host>:<port>/ws` (default port `47821`). One text frame = one JSON message,
maximum 256 KiB. Binary frames are rejected.

## TLS and trust

The server uses a self-signed RSA certificate generated on first start. Clients do **not** validate it against a CA and
do **not** check the host name; they **pin the SHA-256 fingerprint of the certificate (DER bytes, upper-case hex)**.
The fingerprint reaches the phone through the pairing QR code (or UDP discovery + user confirmation for manual pairing).
A connection whose certificate hash differs from the pinned value must be refused.

## Envelope

```json
{ "version": 1, "type": "terminal.execute", "requestId": "unique-id", "payload": { "shell": "powershell", "command": "Get-Process" } }
```

* `version` — must be `1`, otherwise `error/unsupported_version`.
* `type` — message type, max 64 chars.
* `requestId` — set by the client on requests (max 64 chars, **must be unique per connection**; a reused id is rejected
  with `duplicate_request`). The server echoes it in the reply.
* Replies: `{"type":"response","requestId":…,"payload":{…}}` or `{"type":"error","requestId":…,"payload":{"code":…,"message":…}}`.
* Pushes (server → client, no `requestId`): `monitor.snapshot`, `terminal.output`, `auth.permissions`, `session.expired`.
* Null-valued fields are omitted from payloads.

### Error codes

`malformed`, `unsupported_version`, `too_large`, `rate_limited`, `unauthenticated`, `auth_timeout`, `auth_failed`,
`duplicate_request`, `unknown_type`, `forbidden`, `invalid_payload`, `internal_error`, `not_found`, `shell_not_running`,
`terminal_start_failed`, `terminal_error`, `confirmation_invalid`, `confirmation_required`, `action_failed`,
`invalid_shortcut`, `launch_failed`, `launch_cancelled`.

## Authentication

The **first** message on a connection must be `auth.pair` or `auth.login` within 10 s, otherwise the server sends
`auth_timeout` and closes. After 5 failed attempts from one IP within 5 minutes the IP is locked out (`rate_limited`).

| type | payload | response payload |
|---|---|---|
| `auth.pair` | `code` (8 digits, one-time, expires after 5 min), `deviceName`, `deviceModel?` | `deviceId`, `token` (store it securely; shown **once**), `permissions[]`, `computerName`, `serverVersion`, `sessionExpiresUtc`, `protocol` |
| `auth.login` | `deviceId`, `token` | same without `token` |

Permissions (strings): `monitoring`, `terminal`, `launch`, `power`, `admin`.
Sessions expire (default 12 h): the server pushes `session.expired` and closes; the client logs in again.
If the PC owner changes permissions, the server pushes `auth.permissions {permissions[]}`. A revoked device is
disconnected immediately and `auth.login` fails with `auth_failed`.

Heartbeat: send `ping` every ~10 s; the server closes connections idle for 60 s.

## Messages

### General (any authenticated device)
* `ping` → `{serverTimeUtc}`
* `server.info` → `{computerName, permissions[], protocol, uptimeSeconds, macAddresses:[{name, mac, ipv4, broadcast, wireless}]}`

### Monitoring (`monitoring`)
* `monitor.subscribe` → `{intervalMs, snapshot?}`; afterwards the server pushes `monitor.snapshot {snapshot}` every 0.5–10 s (default 1.5 s)
* `monitor.unsubscribe`, `monitor.snapshot` (one-off read) → `{snapshot}`
* `wol.info` → `{adapters:[{name, mac, ipv4, broadcast, wireless, wakeOnLanEnabled?}], note}`
* `power.status` → `{scheduled, action?, atUtc?}`

Snapshot:
```json
{ "timeUtc":"…", "uptimeSeconds":123,
  "cpu":{"model":"…","usage":12.5,"cores":[..per logical core %..],"logicalCores":8,"physicalCores":4,"frequencyMhz":3400,"temperatureC":55.0},
  "gpus":[{"name":"…","vendor":"NVIDIA|AMD|Intel","usage":7,"vramUsedMb":3439,"vramTotalMb":8188,"vramPercent":42,"temperatureC":50}],
  "ram":{"totalBytes":…,"usedBytes":…,"availableBytes":…,"percent":61},
  "disks":[{"id":"C:","label":"System","fileSystem":"NTFS","model":"…","totalBytes":…,"usedBytes":…,"freeBytes":…,"percent":33,"readBps":0,"writeBps":0,"temperatureC":35}] }
```
Any metric the hardware/driver does not expose is **absent** (never faked): `cpu.temperatureC` needs the server to run as
administrator; `usage`, `frequencyMhz`, GPU and disk fields can be absent. The UI must show "n/a".

### Terminal (`terminal`) — `shell` is `"cmd"` or `"powershell"`
Each shell is one persistent ConPTY session on the PC (visible window there). The server runs a terminal emulator and
sends **rendered screen state**, not raw VT bytes.

* `terminal.open {shell, cols?, rows?}` → **frame** (starts the shell the first time) and subscribes this connection to `terminal.output` pushes.
* `terminal.execute {shell, command}` — types `command` + Enter into the running shell (multi-line allowed; control characters rejected).
* `terminal.input {shell, data}` — raw input (keystrokes/escape sequences, e.g. `"\u0003"` Ctrl+C, `"\u001b[A"` up-arrow), ≤ 8 KiB.
* `terminal.interrupt {shell}` — Ctrl+C. `terminal.clear {shell}` — wipes screen+scrollback and redraws the prompt.
* `terminal.resize {shell, cols(20-400), rows(5-200)}` — resizes the session (PC window follows).
* `terminal.restart {shell, cols?, rows?}` → frame (explicit recreate; an exited shell is never recreated automatically).
* `terminal.close {shell}` (unsubscribe), `terminal.status` → `{sessions:[{shell, started, running, exitCode?, pid}]}`

Frame (reply to open/restart and payload of every `terminal.output` push, ~25/s while output changes):
```json
{ "shell":"cmd","seq":7,"full":true,"running":true,"exitCode":null,"title":"…","cols":100,"rows":30,
  "cursor":{"x":3,"y":5,"visible":true},"alt":false,
  "scrollbackTotal":120,"scrollbackStart":0,"addedFrom":0,
  "added":[ROW,…],"screen":[ROW × rows] }
```
`ROW` = array of runs `[text, fg, bg, flags]`; `fg`/`bg` are `-1` (theme default) or `0xRRGGBB` as an integer;
`flags` bit mask 1 = bold, 2 = dim, 4 = italic, 8 = underline (inverse is already applied by the server).
Trailing blanks are trimmed; an empty row is `[]`.

Client algorithm: keep `lines` (scrollback, absolute index = position + `scrollbackStart`). On a frame:
if `full` **or** `scrollbackTotal` < known total **or** `addedFrom` > known total → replace the buffer with `added`
(starting at index `addedFrom`); otherwise append the entries of `added` whose absolute index (`addedFrom + i`) ≥ known
total. Then drop lines with index < `scrollbackStart`. Set known total = `scrollbackTotal`. Render `lines` followed by
`screen`; the cursor is at (`cursor.x`, `cursor.y`) inside `screen`. When `running` is false show "session ended"
with a restart button.

### System actions (`power`)
Restart, shutdown and sleep are two-step; **the server enforces confirmation**:
* `power.request {action:"restart"|"shutdown"|"sleep", delaySeconds?(5-600, default 30)}` → `{confirmationToken, action, delaySeconds, expiresInSeconds:30}` (nothing happens yet)
* `power.confirm {token}` → restart/shutdown: `{scheduled:true, action, delaySeconds, atUtc}`; sleep: `{sleeping:true}`. Token is single-use, bound to the device.
* `power.cancel` → `{cancelled, message}` (runs `shutdown /a`)
* `power.lock` → `{locked:true}` (immediate)

### Shortcuts (`launch`; editing needs `admin`)
* `shortcuts.list` → `{shortcuts:[{id,name,description,icon,path,arguments,workingDirectory,runMode,confirm}], elevatedAllowed}`
* `shortcuts.run {id, confirmed?}` → `{launched, name}`; when `confirm` is true and `confirmed` is not, error `confirmation_required`
* `shortcuts.save {shortcut:{id?,name,description,icon,path,arguments,workingDirectory,runMode,confirm}}` (admin) → `{shortcut}`; no `id` = add
* `shortcuts.delete {id}` (admin), `shortcuts.reorder {ids:[all ids in new order]}` (admin)

`runMode`: `normal|minimized|maximized|hidden|elevated` (elevated only if enabled on the PC; Windows shows a UAC prompt there).
`path`: absolute path or bare name of an `.exe/.msc/.cpl/.lnk`, or the URI `ms-settings:…`. Scripts are not launchable.
Icons are free identifiers; the apps map known ones (`apps, monitor, memory, settings, list, network, chart, web, code, build`).

## Discovery (UDP)

Broadcast the ASCII datagram `PCRC_DISCOVER_V1` to UDP port `47822`. Servers on the LAN answer (unicast to the sender):
`{"app":"pc-remote-center","version":1,"name":"<computer>","port":47821,"fingerprint":"<SHA256 hex>"}`.
A discovered server is **not trusted**: pairing/login still requires the code or token and the pinned fingerprint
(for a discovered server not yet paired, show the fingerprint and let the user compare it with the PC).

## Pairing QR code

`pcrc://pair?v=1&h=192.168.1.20,10.0.0.5&p=47821&c=12345678&f=<SHA256 hex>&n=<computer name, url-encoded>`
(`h` = comma separated addresses to try, `c` = one-time code). Scanning it gives the client everything for `auth.pair`.
