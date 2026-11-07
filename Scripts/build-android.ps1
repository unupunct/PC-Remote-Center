<#
.SYNOPSIS  Builds the Android app (debug by default) with the project-local toolchain.
.EXAMPLE   pwsh -File Scripts\build-android.ps1                # debug APK
.EXAMPLE   pwsh -File Scripts\build-android.ps1 -Release       # release APK (signed if AndroidClient\keystore.properties exists, else unsigned)
.EXAMPLE   pwsh -File Scripts\build-android.ps1 -Test          # JVM unit tests first
Environment overrides: JAVA_HOME (JDK 17), ANDROID_HOME / ANDROID_SDK_ROOT (SDK with platform 35 + build-tools 35.0.0).
No signing secrets are stored in the repository: provide your own keystore.properties (see Documentation\INSTALLATION.md).
#>
param([switch]$Release, [switch]$Test, [string]$JavaHome, [string]$SdkDir)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$android = Join-Path $root 'AndroidClient'

# --- locate JDK 17
if (-not $JavaHome) { $JavaHome = $env:JAVA_HOME }
if (-not $JavaHome -or -not (Test-Path (Join-Path $JavaHome 'bin\java.exe'))) {
    $cands = @('D:\MyAIProjects\blacksite\.tools\jdk', (Join-Path $root '.tools\jdk'), "$env:ProgramFiles\Eclipse Adoptium\jdk-17*", "$env:ProgramFiles\Microsoft\jdk-17*", "$env:ProgramFiles\Java\jdk-17*")
    $JavaHome = $cands | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } | Where-Object { Test-Path (Join-Path $_.FullName 'bin\java.exe') } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $JavaHome) { throw 'JDK 17 not found. Set JAVA_HOME or pass -JavaHome.' }

# --- locate Android SDK
if (-not $SdkDir) { $SdkDir = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT } }
if (-not $SdkDir -or -not (Test-Path $SdkDir)) {
    $SdkDir = @('D:\MyAIProjects\blacksite\.tools\android-sdk', "$env:LOCALAPPDATA\Android\Sdk") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $SdkDir) { throw 'Android SDK not found. Set ANDROID_HOME or pass -SdkDir.' }

$env:JAVA_HOME = $JavaHome; $env:ANDROID_HOME = $SdkDir; $env:ANDROID_SDK_ROOT = $SdkDir
Write-Host "JDK: $JavaHome`nSDK: $SdkDir" -ForegroundColor Cyan

$lp = Join-Path $android 'local.properties'
"sdk.dir=" + ($SdkDir -replace '\\', '\\' -replace ':', '\:') | Set-Content $lp -Encoding ASCII

Push-Location $android
try {
    $gradlew = Join-Path $android 'gradlew.bat'
    if (-not (Test-Path $gradlew)) { throw 'gradlew.bat missing in AndroidClient.' }
    if ($Test) { & $gradlew --no-daemon testDebugUnitTest; if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' } }
    $task = if ($Release) { 'assembleRelease' } else { 'assembleDebug' }
    & $gradlew --no-daemon $task
    if ($LASTEXITCODE -ne 0) { throw "Gradle $task failed." }
} finally { Pop-Location }

Get-ChildItem (Join-Path $android 'app\build\outputs\apk') -Recurse -Filter *.apk | ForEach-Object {
    Write-Host ("APK: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
}
