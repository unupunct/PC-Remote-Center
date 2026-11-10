# Installation

## Requirements
* **PC:** Windows 10 1809 or newer / Windows 11, x64. Nothing else to install – the server is self-contained (.NET runtime included).
* **Phone:** Android 8.0 (API 26) or newer, on the same network as the PC (or connected through a VPN).

## 1. Install the Windows server
Download from the GitHub *Releases* page and pick one:

* **`PCRemoteCenter-Server-Setup-<version>.exe`** – installer, per-user, **no administrator rights needed**. Optional: desktop shortcut, start with Windows.
* **`PCRemoteCenter-Server-<version>-win-x64-portable.zip`** – unzip anywhere and run `RemoteControlServer.exe`.

Check the download against `SHA256SUMS.txt`:
```powershell
Get-FileHash .\PCRemoteCenter-Server-Setup-1.1.0.exe -Algorithm SHA256
```
Windows SmartScreen may warn about an unrecognised publisher (the binaries are not code-signed): *More info → Run anyway*.

On first start the server creates `%APPDATA%\PCRemoteCenter` (database, TLS certificate, logs) and starts listening on TCP **47821**.
The window minimises to the system-tray icon; the server keeps running.

## 2. Allow it through the firewall (once, with your approval)
*Network Settings → Create firewall rule…* (UAC prompt; private networks and local subnet only). See `NETWORKING.md`.

## 3. Install the Android app
Download **`RemoteControlCenter-<version>-release.apk`** (or `-debug.apk`) to the phone and open it. Android asks you to allow
installs from that source ("Install unknown apps"); allow it only for the browser/file manager you used.
Alternatively via USB debugging: `adb install -r RemoteControlCenter-<version>-release.apk`.

## 4. Pair
1. PC: *Connected Devices* → tick what the phone may do → **Generate pairing code** (QR + 8 digits, valid 5 min).
2. Phone: *Add PC* → **Scan QR** (or pick the PC from the discovery list / enter IP and port, compare the fingerprint, type the code).
3. The PC shows a notification, the phone shows the dashboard. Revoke at any time from *Connected Devices*.

## Building from source
Requirements: .NET 10 SDK (Windows), JDK 17, Android SDK 35, Gradle 8.10 (wrapper included), Inno Setup 6 (installer only).

```powershell
# Windows server: tests + self-contained publish  ->  dist\server\RemoteControlServer.exe
pwsh -File Scripts\build-windows.ps1

# Android APK  ->  AndroidClient\app\build\outputs\apk\...
pwsh -File Scripts\build-android.ps1            # debug
pwsh -File Scripts\build-android.ps1 -Release   # release (signed with YOUR keystore, see below)

# Installer + portable zip + APKs + SHA256SUMS  ->  dist\release
pwsh -File Scripts\package-release.ps1
```

### Signing the release APK
No signing key is shipped or generated. Create your own once:
```powershell
keytool -genkeypair -v -keystore my-release.jks -alias pcremote -keyalg RSA -keysize 4096 -validity 10000
```
then create `AndroidClient\keystore.properties` (git-ignored):
```
storeFile=C:\\path\\to\\my-release.jks
storePassword=...
keyAlias=pcremote
keyPassword=...
```
Without that file the release build is produced **unsigned** (it cannot be installed until signed).

## Uninstalling
Windows *Settings → Apps*. The uninstaller asks whether to delete settings and paired devices. On the phone, remove the PCs
in the app and uninstall it normally.
