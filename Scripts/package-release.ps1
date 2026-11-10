<#
.SYNOPSIS  Produces the release artefacts in dist\release: installer, portable zip, APK(s) and SHA256SUMS.
.EXAMPLE   pwsh -File Scripts\package-release.ps1                 # Windows build + installer + zip (+ APK if built)
.EXAMPLE   pwsh -File Scripts\package-release.ps1 -SkipTests -SkipAndroid
APK signing: build-android.ps1 -Release signs with the user's own keystore (AndroidClient\keystore.properties); no key is bundled.
#>
param([string]$Version = '1.2.0', [switch]$SkipTests, [switch]$SkipAndroid, [switch]$SkipWindowsBuild, [switch]$IncludeDebugApk)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dist = Join-Path $root 'dist'
$rel = Join-Path $dist 'release'
New-Item -ItemType Directory -Force $rel | Out-Null

if (-not $SkipWindowsBuild) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') -SkipTests:$SkipTests
}
if (-not (Test-Path (Join-Path $dist 'server\RemoteControlServer.exe'))) { throw 'dist\server is missing - run build-windows.ps1 first.' }

# --- installer (Inno Setup 6, per-user install)
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" (Join-Path $root 'Installer\RemoteControlServer.iss') | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }
    Copy-Item (Join-Path $dist "installer\PCRemoteCenter-Server-Setup-$Version.exe") $rel -Force
} else { Write-Warning 'Inno Setup 6 not found - installer skipped.' }

# --- portable zip
$zip = Join-Path $rel "PCRemoteCenter-Server-$Version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $dist 'server\*') -DestinationPath $zip -CompressionLevel Optimal

# --- APK(s)
if (-not $SkipAndroid) {
    $apkDir = Join-Path $root 'AndroidClient\app\build\outputs\apk'
    $signed = Join-Path $apkDir 'release\app-release.apk'
    if (Test-Path $signed) { Copy-Item $signed (Join-Path $rel "RemoteControlCenter-$Version.apk") -Force }
    elseif (Test-Path (Join-Path $apkDir 'release\app-release-unsigned.apk')) {
        Write-Warning 'Only an UNSIGNED release APK exists (no AndroidClient\keystore.properties) - it cannot be installed; not adding it to the release.'
    }
    else { Write-Warning 'No release APK found - run Scripts\build-android.ps1 -Release first.' }
    if ($IncludeDebugApk) { Copy-Item (Join-Path $apkDir 'debug\app-debug.apk') (Join-Path $rel "RemoteControlCenter-$Version-debug.apk") -Force }
}

# --- checksums
$lines = Get-ChildItem $rel -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
$lines | Set-Content (Join-Path $rel 'SHA256SUMS.txt')
Write-Host "`nRelease artefacts in ${rel}:" -ForegroundColor Green
Get-ChildItem $rel -File | ForEach-Object { "{0,10:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name }
