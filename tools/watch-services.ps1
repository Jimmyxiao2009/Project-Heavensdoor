param()
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$rsDir = Join-Path $root "server\QQReborn.RealServer\bin\Debug\net10.0"
$rsExe = Join-Path $rsDir "QQReborn.RealServer.exe"
$signProj = Join-Path $root "tools\LocalSignProxy\LocalSignProxy.csproj"
$logDir = Join-Path $root "tools\runtime_logs"
$log = Join-Path $logDir "watch-services.log"

function Log([string]$m) {
    $t = (Get-Date).ToString("s")
    Add-Content -Path $log -Value "$t $m"
}

function HttpOk([string]$url) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 2
        return $r.StatusCode -eq 200
    } catch {
        return $false
    }
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Log "watch started"

while ($true) {
    try {
        if (-not (HttpOk "http://127.0.0.1:8765/")) {
            Get-Process QQReborn.RealServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep 1
            if (Test-Path $rsExe) {
                Start-Process -FilePath $rsExe -WorkingDirectory $rsDir -WindowStyle Hidden
                Log "started RealServer"
            } else {
                Log "missing RealServer exe: $rsExe"
            }
        }

        if (-not (HttpOk "http://127.0.0.1:18488/health")) {
            Start-Process -FilePath "dotnet" `
                -ArgumentList @("run","--project",$signProj,"-c","Release","--no-launch-profile") `
                -WorkingDirectory (Split-Path $signProj) `
                -WindowStyle Hidden
            Log "started SignProxy"
        }
    } catch {
        Log ("watch error: " + $_.Exception.Message)
    }
    Start-Sleep 5
}
