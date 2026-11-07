<#
.SYNOPSIS  Builds the Windows server: runs the tests (optional) and publishes a self-contained win-x64 build.
.EXAMPLE   pwsh -File Scripts\build-windows.ps1
.EXAMPLE   pwsh -File Scripts\build-windows.ps1 -Configuration Debug -SkipTests
Output: dist\server\RemoteControlServer.exe (+ native side files: conpty.dll, OpenConsole.exe, e_sqlite3.dll, WPF natives)
#>
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$Output = (Join-Path $PSScriptRoot '..\dist\server')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$sln = Join-Path $root 'WindowsServer\RemoteControlServer.sln'

Write-Host "== dotnet $(dotnet --version)" -ForegroundColor Cyan
if (-not $SkipTests) {
    Write-Host '== Running server tests (they use the real hardware, shells and a 1-hour shutdown that is aborted immediately)' -ForegroundColor Cyan
    dotnet test $sln -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not publishing.' }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
Write-Host '== Publishing self-contained win-x64' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'WindowsServer\RemoteControlServer\RemoteControlServer.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -o $Output --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $Output 'RemoteControlServer.exe'
foreach ($f in 'RemoteControlServer.exe', 'conpty.dll', 'OpenConsole.exe') {
    if (-not (Test-Path (Join-Path $Output $f))) { throw "Missing $f in the publish output." }
}
$size = [math]::Round(((Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "OK: $exe  (folder $size MB)" -ForegroundColor Green
