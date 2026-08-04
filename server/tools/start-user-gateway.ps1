# QQ Reborn — 本机 NapCat 网关（MC 开服式）
# 1) 本机已登录 NTQQ + NapCat
# 2) 运行本脚本启动 RealServer (backend=napcat) 监听 :8765
# 3) 出门：用 OpenFrp/Frp 映射 127.0.0.1:8765（见 docs/USER-GATEWAY-OPENFRP.md）
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\start-user-gateway.ps1
#   powershell -ExecutionPolicy Bypass -File tools\start-user-gateway.ps1 -NapCatHttp http://127.0.0.1:3000

param(
    [string]$NapCatHttp = "http://127.0.0.1:3000",
    [string]$NapCatWs = "ws://127.0.0.1:3001",
    [string]$NapCatToken = "",
    [string]$AccessPassword = "",
    [string]$Configuration = "Debug",
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"
$ServerRoot = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $ServerRoot
$proj = Join-Path $root "server\QQReborn.RealServer\QQReborn.RealServer.csproj"

function Test-Url([string]$url) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 2
        return $true
    } catch { return $false }
}

Write-Host ""
Write-Host "========== QQ Reborn 本机网关 =========="
Write-Host "  模式     : localGateway + napcat"
Write-Host "  NapCat   : $NapCatHttp"
Write-Host "  Wire     : ws://127.0.0.1:$Port/ws"
Write-Host "  出门     : OpenFrp/Frp 映射 127.0.0.1:$Port"
Write-Host "  文档     : docs/USER-GATEWAY-SAKURAFRP.md"
Write-Host "========================================"
Write-Host ""

# Soft check NapCat HTTP (best-effort: many builds use different paths)
Write-Host "Checking NapCat HTTP..."
$napOk = $false
foreach ($path in @("/get_login_info", "/", "/status")) {
    if (Test-Url ($NapCatHttp.TrimEnd('/') + $path)) { $napOk = $true; break }
}
if (-not $napOk) {
    Write-Host "[!] 暂时连不上 $NapCatHttp — 请确认 NTQQ+NapCat 已启动并登录。"
    Write-Host "    仍会启动 RealServer；登录失败时请回头检查 NapCat。"
} else {
    Write-Host "[ok] NapCat HTTP 有响应"
}

$env:QQREBORN_BACKEND = "napcat"
$env:QQREBORN_MODE = "localGateway"
$env:NAPCAT_HTTP = $NapCatHttp
$env:NAPCAT_WS = $NapCatWs
$env:QQREBORN_ACCESS_PASSWORD = $AccessPassword
$env:QQReborn__AccessPassword = $AccessPassword
if ($NapCatToken) { $env:NAPCAT_TOKEN = $NapCatToken }

Write-Host "Building RealServer..."
dotnet build $proj -c $Configuration -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Free port if stale
try {
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        if ($c.OwningProcess) {
            Write-Host "Stopping PID $($c.OwningProcess) on port $Port"
            Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
        }
    }
} catch {}

Write-Host ""
Write-Host "Starting gateway. Keep this window open (like a Minecraft server console)."
Write-Host "  在家 Shell 服务器填: 127.0.0.1"
Write-Host "  出门 Shell 服务器填: OpenFrp 面板的访问主机"
Write-Host "  只穿透端口 $Port ，不要穿透 NapCat 3000/3001"
Write-Host ""

dotnet run --project $proj -c $Configuration --no-build --no-launch-profile
