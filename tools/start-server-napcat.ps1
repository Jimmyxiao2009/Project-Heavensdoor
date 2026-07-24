# Start RealServer with NapCat backend.
# Prerequisites: NTQQ + NapCat logged in, HTTP + event WS enabled.
param(
    [string]$Http = "http://127.0.0.1:3000",
    [string]$Ws = "ws://127.0.0.1:3001",
    [string]$Token = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "server\QQReborn.RealServer\QQReborn.RealServer.csproj"

$env:QQREBORN_BACKEND = "napcat"
$env:NAPCAT_HTTP = $Http
$env:NAPCAT_WS = $Ws
if ($Token) { $env:NAPCAT_TOKEN = $Token }

Write-Host "Building RealServer..."
dotnet build $proj -c $Configuration -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "Starting RealServer backend=napcat"
Write-Host "  NAPCAT_HTTP=$Http"
Write-Host "  NAPCAT_WS=$Ws"
Write-Host "  App still connects to ws://127.0.0.1:8765/ws"
Write-Host ""

dotnet run --project $proj -c $Configuration --no-build --no-launch-profile
