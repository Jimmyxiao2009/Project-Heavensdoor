# Launch the WPF Server Host (QQ Reborn 管家) — NapCat local gateway only.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1
#   powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1 -Publish

param(
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "server\QQReborn.ServerHost\QQReborn.ServerHost.csproj"

if (-not (Test-Path $proj)) { throw "Missing $proj" }

Write-Host "Building ServerHost..."
dotnet build $proj -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($Publish) {
    $out = Join-Path $root "publish\ServerHost"
    Write-Host "Publishing self-contained to $out ..."
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    $rsOut = Join-Path $out "RealServer"
    Write-Host "Publishing RealServer (self-contained) to $rsOut ..."
    # Self-contained so installed/portable copies don't require a shared .NET runtime.
    dotnet publish (Join-Path $root "server\QQReborn.RealServer\QQReborn.RealServer.csproj") `
        -c Release -r win-x64 --self-contained true -o $rsOut -v q

    Write-Host ""
    Write-Host "Done. Run: $out\QQReborn.ServerHost.exe"
    Write-Host "MSI: powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1"
    Start-Process (Join-Path $out "QQReborn.ServerHost.exe")
    exit 0
}

$exe = Join-Path $root "server\QQReborn.ServerHost\bin\Release\net10.0-windows\QQReborn.ServerHost.exe"
if (-not (Test-Path $exe)) {
    $exe = Get-ChildItem (Join-Path $root "server\QQReborn.ServerHost\bin\Release") -Recurse -Filter "QQReborn.ServerHost.exe" |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $exe -or -not (Test-Path $exe)) { throw "Host exe not found after build" }

Write-Host "Starting $exe"
Start-Process $exe
