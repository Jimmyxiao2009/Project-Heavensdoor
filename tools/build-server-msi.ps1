# Publish QQ Reborn ServerHost + self-contained RealServer, then build an x64 MSI.
#
# Prerequisites:
#   - .NET SDK (net10)
#   - WiX 5 CLI:  dotnet tool install -g wix
#                 wix extension add WixToolset.UI.wixext/5.0.2
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -SkipPublish
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -Version 0.1.0.2

param(
    [string]$Version = "0.1.0.2",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing command '$Name'. Install WiX:  dotnet tool install -g wix"
    }
}

Require-Command "dotnet"
Require-Command "wix"

$publishDir = Join-Path $root "publish\ServerHost"
$msiDir = Join-Path $root "publish\msi"
$wxs = Join-Path $root "installer\ServerHost\Package.wxs"

if (-not (Test-Path $wxs)) { throw "Missing $wxs" }

# Normalize version to MSI 4-part
$parts = $Version.Split('.')
while ($parts.Count -lt 4) { $parts += "0" }
$msiVersion = ($parts[0..3] -join '.')

if (-not $SkipPublish) {
    Write-Host "==> Publishing ServerHost (self-contained win-x64 single-file)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    dotnet publish (Join-Path $root "server\QQReborn.ServerHost\QQReborn.ServerHost.csproj") `
        -c Release -r win-x64 --self-contained true `
        -o $publishDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$msiVersion `
        -v q
    if ($LASTEXITCODE -ne 0) { throw "ServerHost publish failed" }

    # RealServer must be self-contained for MSI end-users (no shared .NET install required).
    $rsOut = Join-Path $publishDir "RealServer"
    Write-Host "==> Publishing RealServer (self-contained win-x64)..."
    dotnet publish (Join-Path $root "server\QQReborn.RealServer\QQReborn.RealServer.csproj") `
        -c Release -r win-x64 --self-contained true `
        -o $rsOut `
        -p:Version=$msiVersion `
        -v q
    if ($LASTEXITCODE -ne 0) { throw "RealServer publish failed" }
}
else {
    if (-not (Test-Path (Join-Path $publishDir "QQReborn.ServerHost.exe"))) {
        throw "SkipPublish set but $publishDir\QQReborn.ServerHost.exe missing. Run without -SkipPublish."
    }
}

# Patch version into a temp wxs (keep source as template default)
$tmpWxsDir = Join-Path $root "installer\ServerHost\_build"
New-Item -ItemType Directory -Force -Path $tmpWxsDir | Out-Null
$tmpWxs = Join-Path $tmpWxsDir "Package.wxs"
$wxsText = Get-Content $wxs -Raw -Encoding UTF8
# Only Product Version=… — not InstallerVersion=
$wxsText = [regex]::Replace($wxsText, '(?<![A-Za-z])Version="[0-9.]+"', "Version=`"$msiVersion`"")
Set-Content -Path $tmpWxs -Value $wxsText -Encoding UTF8

New-Item -ItemType Directory -Force -Path $msiDir | Out-Null
$msiName = "QQReborn.ServerHost-$msiVersion-x64.msi"
$msiPath = Join-Path $msiDir $msiName

Write-Host "==> Building MSI: $msiPath"
$pubAbs = (Resolve-Path $publishDir).Path
Push-Location $tmpWxsDir
try {
    wix build "Package.wxs" `
        -arch x64 `
        -bindpath "Publish=$pubAbs" `
        -out $msiPath `
        -pdbtype none `
        -defaultcompressionlevel high
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
}
finally {
    Pop-Location
}

$item = Get-Item $msiPath
Write-Host ""
Write-Host "Done."
Write-Host "  MSI : $($item.FullName)"
Write-Host "  Size: $([math]::Round($item.Length / 1MB, 1)) MB"
Write-Host "  Install: msiexec /i `"$($item.FullName)`""
Write-Host "  Quiet:   msiexec /i `"$($item.FullName)`" /qn"
Write-Host "  Remove:  msiexec /x `"$($item.FullName)`" /qn"
