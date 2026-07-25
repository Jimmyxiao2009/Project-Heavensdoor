# Publish QQ Reborn ServerHost + self-contained RealServer + NapCat Shell, then build an x64 MSI.
#
# Prerequisites:
#   - .NET SDK (net10)
#   - WiX 5 CLI:  dotnet tool install -g wix
#   - NapCat.Shell (optional but recommended): set NAPCAT_SHELL, or place under third_party\NapCat\shell
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -SkipPublish
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -Version 0.1.0.3
#   powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -NapCatShell "D:\NapCat.Shell"

param(
    [string]$Version = "0.1.0.4",
    [switch]$SkipPublish,
    [string]$NapCatShell = "",
    [switch]$SkipNapCat,
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing command '$Name'. Install WiX:  dotnet tool install -g wix"
    }
}

function Test-NapCatShell([string]$Dir) {
    if (-not $Dir -or -not (Test-Path $Dir)) { return $false }
    return (Test-Path (Join-Path $Dir "NapCatWinBootMain.exe")) `
        -and (Test-Path (Join-Path $Dir "NapCatWinBootHook.dll")) `
        -and (Test-Path (Join-Path $Dir "napcat.mjs"))
}

function Resolve-NapCatShell {
    param([string]$Explicit)
    $candidates = @(
        $Explicit,
        $env:NAPCAT_SHELL,
        (Join-Path $root "third_party\NapCat\shell"),
        (Join-Path $root "third_party\NapCat"),
        "D:\NapCat.Shell",
        "C:\NapCat.Shell",
        (Join-Path $env:LOCALAPPDATA "Temp\grok-goal-7c849b0de48f\implementer\NapCat\shell")
    ) | Where-Object { $_ }

    foreach ($c in $candidates) {
        if (Test-NapCatShell $c) { return (Resolve-Path $c).Path }
        $nested = Join-Path $c "shell"
        if (Test-NapCatShell $nested) { return (Resolve-Path $nested).Path }
    }

    # Zip next to a known shell parent
    $zipCandidates = @(
        (Join-Path $root "third_party\NapCat\NapCat.Shell.zip"),
        (Join-Path $env:LOCALAPPDATA "Temp\grok-goal-7c849b0de48f\implementer\NapCat\NapCat.Shell.zip")
    )
    foreach ($zip in $zipCandidates) {
        if (-not (Test-Path $zip)) { continue }
        $extractTo = Join-Path $root "third_party\NapCat\_extracted"
        Write-Host "==> Extracting NapCat from $zip ..."
        if (Test-Path $extractTo) { Remove-Item $extractTo -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $extractTo | Out-Null
        Expand-Archive -Path $zip -DestinationPath $extractTo -Force
        # Zip may expand to shell\ or NapCat.Shell\ or flat
        $found = Get-ChildItem $extractTo -Recurse -Filter "NapCatWinBootMain.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) {
            $dir = $found.Directory.FullName
            if (Test-NapCatShell $dir) { return $dir }
        }
    }
    return $null
}

function Copy-NapCatShell {
    param(
        [string]$Source,
        [string]$Dest
    )
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null

    # Prefer robocopy for speed; fall back to Copy-Item
    $xd = @("cache", "logs", ".git")
    $robolog = Join-Path $env:TEMP "qqreborn-napcat-robocopy.log"
    $rcArgs = @($Source, $Dest, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/nc", "/ns", "/np", "/XD") + $xd + @("/XF", "*.log", "/R:1", "/W:1", "/LOG:$robolog")
    & robocopy @rcArgs | Out-Null
    # robocopy exit 0-7 = success-ish
    if ($LASTEXITCODE -ge 8) {
        Write-Host "robocopy failed ($LASTEXITCODE), using Copy-Item..."
        Copy-Item -Path (Join-Path $Source "*") -Destination $Dest -Recurse -Force
        foreach ($d in $xd) {
            $p = Join-Path $Dest $d
            if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    # Seed product OneBot templates (always overwrite package defaults)
    $cfg = Join-Path $Dest "config"
    New-Item -ItemType Directory -Force -Path $cfg | Out-Null
    $tpl = Join-Path $root "installer\ServerHost\napcat-config"
    foreach ($name in @("onebot11.json", "napcat.json")) {
        $src = Join-Path $tpl $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $cfg $name) -Force
        }
    }
    # Also keep templates for ServerHost to re-seed runtime copies
    $tplOut = Join-Path $Dest "config-templates"
    New-Item -ItemType Directory -Force -Path $tplOut | Out-Null
    if (Test-Path $tpl) {
        Copy-Item (Join-Path $tpl "*") $tplOut -Force
    }
}

Require-Command "dotnet"
if (-not $SkipMsi) { Require-Command "wix" }

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

    # Always ship gateway + napcat appsettings next to RealServer (publish already copies;
    # re-assert NapCat defaults from repo source).
    $rsSrc = Join-Path $root "server\QQReborn.RealServer"
    foreach ($cfgName in @("appsettings.json", "appsettings.NapCat.json", "appsettings.Gateway.json")) {
        $srcCfg = Join-Path $rsSrc $cfgName
        if (Test-Path $srcCfg) {
            Copy-Item $srcCfg (Join-Path $rsOut $cfgName) -Force
        }
    }
}
else {
    if (-not (Test-Path (Join-Path $publishDir "QQReborn.ServerHost.exe"))) {
        throw "SkipPublish set but $publishDir\QQReborn.ServerHost.exe missing. Run without -SkipPublish."
    }
}

# Stage NapCat shell + OneBot templates into publish\ServerHost\NapCat
$napcatOut = Join-Path $publishDir "NapCat"
if (-not $SkipNapCat) {
    $shell = Resolve-NapCatShell -Explicit $NapCatShell
    if ($shell) {
        Write-Host "==> Staging NapCat Shell from: $shell"
        Copy-NapCatShell -Source $shell -Dest $napcatOut
        $boot = Join-Path $napcatOut "NapCatWinBootMain.exe"
        if (-not (Test-Path $boot)) { throw "NapCat staging failed — missing $boot" }
        Write-Host "    OneBot defaults: HTTP 127.0.0.1:3000  WS 127.0.0.1:3001"
    }
    else {
        Write-Host "==> WARNING: NapCat Shell not found. MSI will not include NapCat binaries."
        Write-Host "    Set -NapCatShell or NAPCAT_SHELL, or place files under third_party\NapCat\shell"
        # Still ship config templates so 管家 can write ports when user supplies Shell later
        $tplOnly = Join-Path $napcatOut "config-templates"
        New-Item -ItemType Directory -Force -Path $tplOnly | Out-Null
        $tpl = Join-Path $root "installer\ServerHost\napcat-config"
        if (Test-Path $tpl) { Copy-Item (Join-Path $tpl "*") $tplOnly -Force }
    }
}
else {
    Write-Host "==> SkipNapCat: not bundling NapCat Shell"
}

$hasNapCat = Test-Path (Join-Path $publishDir "NapCat\NapCatWinBootMain.exe")

if ($SkipMsi) {
    Write-Host ""
    Write-Host "Done (SkipMsi)."
    Write-Host "  Publish: $publishDir"
    Write-Host "  NapCat : $(if ($hasNapCat) { 'bundled (OneBot 3000/3001)' } else { 'NOT bundled' })"
    return
}

# Patch version into a temp wxs (keep source as template default)
$tmpWxsDir = Join-Path $root "installer\ServerHost\_build"
New-Item -ItemType Directory -Force -Path $tmpWxsDir | Out-Null
$tmpWxs = Join-Path $tmpWxsDir "Package.wxs"
$wxsText = Get-Content $wxs -Raw -Encoding UTF8
# Only Product Version=… — not InstallerVersion=
$wxsText = [regex]::Replace($wxsText, '(?<![A-Za-z])Version="[0-9.]+"', "Version=`"$msiVersion`"")
Set-Content -Path $tmpWxs -Value $wxsText -Encoding UTF8

# License.rtf for WiX UI if present
$licenseSrc = Join-Path $root "installer\ServerHost\License.rtf"
if (Test-Path $licenseSrc) {
    Copy-Item $licenseSrc (Join-Path $tmpWxsDir "License.rtf") -Force
}

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
Write-Host "  MSI    : $($item.FullName)"
Write-Host "  Size   : $([math]::Round($item.Length / 1MB, 1)) MB"
Write-Host "  Version: $msiVersion"
Write-Host "  NapCat : $(if ($hasNapCat) { 'bundled (OneBot 3000/3001)' } else { 'NOT bundled' })"
Write-Host "  Install: msiexec /i `"$($item.FullName)`""
Write-Host "  Quiet:   msiexec /i `"$($item.FullName)`" /qn"
Write-Host "  Remove:  msiexec /x `"$($item.FullName)`" /qn"
