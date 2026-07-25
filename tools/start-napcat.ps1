# Start NapCat Shell with quick-login UIN (default = main account 1913695019).
# Stages into %LocalAppData%\QQReborn\NapCat and applies OneBot 3000/3001.
param(
    [string]$Uin = "1913695019",
    [string]$NapCatShell = "",
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Test-Shell([string]$Dir) {
    if (-not $Dir -or -not (Test-Path $Dir)) { return $false }
    return (Test-Path (Join-Path $Dir "NapCatWinBootMain.exe"))
}

$candidates = @(
    $NapCatShell,
    (Join-Path $env:LOCALAPPDATA "QQReborn\NapCat"),
    (Join-Path $root "publish\ServerHost\NapCat"),
    (Join-Path $env:LOCALAPPDATA "Temp\grok-goal-7c849b0de48f\implementer\NapCat\shell"),
    "D:\NapCat.Shell",
    "C:\NapCat.Shell"
) | Where-Object { $_ -and (Test-Shell $_) }

$src = $candidates | Select-Object -First 1
if (-not $src) { throw "NapCat shell not found. Pass -NapCatShell or build MSI first." }

$runtime = Join-Path $env:LOCALAPPDATA "QQReborn\NapCat"
if ($src -ne $runtime) {
    Write-Host "Staging NapCat: $src -> $runtime"
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    robocopy $src $runtime /E /XD cache logs .git /XF *.log /NFL /NDL /NJH /NJS /nc /ns /np /R:1 /W:1 | Out-Null
}

$cfgDir = Join-Path $runtime "config"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$tpl = Join-Path $root "installer\ServerHost\napcat-config"
if (Test-Path $tpl) {
    Copy-Item (Join-Path $tpl "onebot11.json") (Join-Path $cfgDir "onebot11.json") -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $tpl "napcat.json") (Join-Path $cfgDir "napcat.json") -Force -ErrorAction SilentlyContinue
    Get-ChildItem $cfgDir -Filter "onebot11*.json" -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item (Join-Path $tpl "onebot11.json") $_.FullName -Force
    }
}

$boot = Join-Path $runtime "NapCatWinBootMain.exe"
$hook = Join-Path $runtime "NapCatWinBootHook.dll"
$qq = "C:\Program Files\Tencent\QQNT\QQ.exe"
if (-not (Test-Path $boot)) { throw "missing $boot" }
if (-not (Test-Path $qq)) { throw "missing $qq" }

$env:NAPCAT_PATCH_PACKAGE = Join-Path $runtime "qqnt.json"
$env:NAPCAT_LOAD_PATH = Join-Path $runtime "loadNapCat.js"
$env:NAPCAT_INJECT_PATH = $hook
$env:NAPCAT_LAUNCHER_PATH = $boot
$env:NAPCAT_MAIN_PATH = Join-Path $runtime "napcat.mjs"

$main = ($env:NAPCAT_MAIN_PATH -replace '\\', '/')
Set-Content -Path $env:NAPCAT_LOAD_PATH -Value "(async () => {await import(`"file:///$main`")})()" -Encoding UTF8

Write-Host "Starting NapCat uin=$Uin from $runtime"
$args = @("`"$qq`"", "`"$hook`"")
if ($Uin) { $args += $Uin }
Start-Process -FilePath $boot -ArgumentList $args -WorkingDirectory $runtime -WindowStyle Minimized

for ($i = 0; $i -lt $WaitSeconds; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:3000/get_login_info" -Method POST -ContentType "application/json" -Body "{}" -TimeoutSec 2
        Write-Host $r.Content
        if (-not $Uin -or $r.Content -match $Uin -or $r.Content -match '"status"\s*:\s*"ok"') {
            Write-Host "NAPCAT_OK"
            exit 0
        }
    } catch {
        if (($i + 1) % 10 -eq 0) { Write-Host "waiting... $($i + 1)s" }
    }
}
throw "NapCat HTTP 3000 not ready within ${WaitSeconds}s (check QQ login window)"
