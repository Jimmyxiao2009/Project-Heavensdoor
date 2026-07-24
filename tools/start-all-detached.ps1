# Detached launcher for QQ-Reborn services + UWP app.
# Survives remote desktop disconnects because processes are started independently.
param(
    [switch]$NoApp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rsDir = Join-Path $root "server\QQReborn.RealServer\bin\Debug\net10.0"
$rsExe = Join-Path $rsDir "QQReborn.RealServer.exe"
$signProj = Join-Path $root "tools\LocalSignProxy\LocalSignProxy.csproj"
$extractSrc = Join-Path $root "_reverse\extract_feeds3.py"
$extractDst = Join-Path $rsDir "extract_feeds3.py"

function Ensure-Running([string]$name) {
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue).Count -gt 0
}

if (-not (Test-Path $rsExe)) { throw "Missing RealServer exe: $rsExe" }
if (-not (Test-Path $signProj)) { throw "Missing sign proxy project: $signProj" }

# Keep extractor next to RealServer for feed parsing.
if (Test-Path $extractSrc) {
    Copy-Item $extractSrc $extractDst -Force
}

# Local sign proxy (dotnet run, detached window)
if (-not (Ensure-Running "dotnet") -or -not (try { (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:18488/health -TimeoutSec 2).StatusCode -eq 200 } catch { $false })) {
    Write-Host "Starting LocalSignProxy..."
    Start-Process -FilePath "dotnet" `
        -ArgumentList @("run","--project",$signProj,"-c","Release","--no-launch-profile") `
        -WorkingDirectory (Split-Path $signProj) `
        -WindowStyle Minimized
}

# RealServer detached
if (-not (Ensure-Running "QQReborn.RealServer")) {
    Write-Host "Starting RealServer..."
    Start-Process -FilePath $rsExe -WorkingDirectory $rsDir -WindowStyle Minimized
}

# Wait for health
for ($i=0; $i -lt 20; $i++) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:8765/" -TimeoutSec 1
        if ($r.StatusCode -eq 200) { break }
    } catch {}
    Start-Sleep -Milliseconds 500
}

if (-not $NoApp) {
    $app = Get-StartApps | Where-Object { $_.Name -eq "QQ Reborn" } | Select-Object -First 1
    if ($null -eq $app) { throw "QQ Reborn is not registered as an AppX package" }
    Write-Host "Starting UWP app..."
    Start-Process explorer.exe -ArgumentList ("shell:AppsFolder\" + $app.AppID)
}

Write-Host "Done."
Write-Host "RealServer : http://127.0.0.1:8765"
Write-Host "SignProxy  : http://127.0.0.1:18488"
