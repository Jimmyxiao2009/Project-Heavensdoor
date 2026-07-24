# Start NapCat Shell quick-login as alt account (default 2901884390). Never uses main account.
param(
    [string]$Uin = "2901884390",
    [string]$NapCatShell = ""
)

$ErrorActionPreference = "Stop"
if ($Uin -eq "1913695019") { throw "Refusing to launch main account 1913695019" }

$candidates = @(
    $NapCatShell,
    (Join-Path $env:LOCALAPPDATA "Temp\grok-goal-7c849b0de48f\implementer\NapCat\shell"),
    "D:\NapCat.Shell",
    "C:\NapCat.Shell"
) | Where-Object { $_ -and (Test-Path $_) }

$shell = $candidates | Select-Object -First 1
if (-not $shell) { throw "NapCat shell not found. Pass -NapCatShell path" }

$boot = Join-Path $shell "NapCatWinBootMain.exe"
$hook = Join-Path $shell "NapCatWinBootHook.dll"
$qq = "C:\Program Files\Tencent\QQNT\QQ.exe"
if (-not (Test-Path $boot)) { throw "missing $boot" }
if (-not (Test-Path $qq)) { throw "missing $qq" }

$env:NAPCAT_PATCH_PACKAGE = Join-Path $shell "qqnt.json"
$env:NAPCAT_LOAD_PATH = Join-Path $shell "loadNapCat.js"
$env:NAPCAT_INJECT_PATH = $hook
$env:NAPCAT_LAUNCHER_PATH = $boot
$env:NAPCAT_MAIN_PATH = Join-Path $shell "napcat.mjs"

$main = ($env:NAPCAT_MAIN_PATH -replace '\\', '/')
Set-Content -Path $env:NAPCAT_LOAD_PATH -Value "(async () => {await import(`"file:///$main`")})()" -Encoding UTF8

Write-Host "Starting NapCat alt uin=$Uin from $shell"
Start-Process -FilePath $boot -ArgumentList @("`"$qq`"", "`"$hook`"", $Uin) -WorkingDirectory $shell -WindowStyle Minimized

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:3000/get_login_info" -Method POST -ContentType "application/json" -Body "{}" -TimeoutSec 2
        Write-Host $r.Content
        if ($r.Content -match $Uin) { Write-Host "NAPCAT_ALT_OK"; exit 0 }
        if ($r.Content -match "1913695019") { throw "main account online" }
    } catch { }
}
throw "NapCat HTTP 3000 not ready"
