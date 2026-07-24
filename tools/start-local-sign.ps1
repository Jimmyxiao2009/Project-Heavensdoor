# Start the local sign proxy on http://127.0.0.1:18488
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\start-local-sign.ps1
#   powershell -ExecutionPolicy Bypass -File tools\start-local-sign.ps1 -Token "your-token" -Upstream "https://sign.lagrangecore.org"

param(
    [string]$Token = "",
    [string]$Upstream = "https://sign.lagrangecore.org",
    [string]$Urls = "http://127.0.0.1:18488"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $PSScriptRoot "LocalSignProxy\LocalSignProxy.csproj"
$cfgPath = Join-Path $PSScriptRoot "LocalSignProxy\appsettings.json"

if (-not (Test-Path $proj)) { throw "Missing $proj" }

# Prefer token from args, else keep whatever is already in appsettings.json.
if ($Token) {
    $json = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $json.SignProxy.UpstreamToken = $Token
    $json.SignProxy.UpstreamUrl = $Upstream
    $json.Urls = $Urls
    $json | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding UTF8
    Write-Host "Updated $cfgPath"
}

Write-Host "Building LocalSignProxy..."
dotnet build $proj -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host ""
Write-Host "============================================================"
Write-Host " LocalSignProxy starting"
Write-Host "   URL      : $Urls"
Write-Host "   Upstream : $Upstream"
Write-Host ""
Write-Host " In QQ Reborn App → 设置 → 账号:"
Write-Host "   使用自建签名服务器 : 开"
Write-Host "   签名服务器 URL     : http://127.0.0.1:18488"
Write-Host "   API Key            : 可留空，或填你的 token"
Write-Host "============================================================"
Write-Host ""

dotnet run --project $proj -c Release --no-build
