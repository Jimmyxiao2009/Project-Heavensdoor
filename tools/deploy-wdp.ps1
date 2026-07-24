# Deploy QQReborn ARM package to a Windows device via WinAppDeployCmd or Device Portal REST.
# Also installs the ARM Microsoft.NET.CoreRuntime.1.1 dependency first (missing this is a
# common "install OK, launch flash-crash" failure on Windows 10 Mobile / ARM).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip 192.168.3.18
#   powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip 192.168.3.18 -Pin <PIN>
#   powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip 192.168.3.18 -ViaDevicePortal
#   powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip 192.168.3.18 -Build

param(
    [string]$Ip = "192.168.3.18",
    [string]$Pin = "",
    [switch]$Build,
    [ValidateSet("Debug","Release")][string]$Configuration = "Debug",
    [switch]$ViaDevicePortal
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appProj = Join-Path $root "shell\QQReborn.App\QQReborn.App.csproj"

function Get-ArmCoreRuntimePackage {
  $candidates = @(
    (Join-Path $root "tools\deps\ARM\Microsoft.NET.CoreRuntime.1.1.appx"),
    (Join-Path $root "shell\QQReborn.App\AppPackages\QQReborn.App_0.1.0.0_ARM_Debug_Test\Dependencies\ARM\Microsoft.NET.CoreRuntime.1.1.appx"),
    "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.NET.CoreRuntime\1.1\AppX\arm\Microsoft.NET.CoreRuntime.1.1.appx"
  )
  foreach ($c in $candidates) {
    if (Test-Path $c) { return (Get-Item $c) }
  }
  return $null
}

function Ensure-ArmDependenciesLayout {
  param([string]$PackageDir)
  $armDepDir = Join-Path $PackageDir "Dependencies\ARM"
  New-Item -ItemType Directory -Force -Path $armDepDir | Out-Null
  $src = Get-ArmCoreRuntimePackage
  if (-not $src) {
    Write-Host "WARN: ARM Microsoft.NET.CoreRuntime.1.1.appx not found on this PC."
    return $null
  }
  $dst = Join-Path $armDepDir "Microsoft.NET.CoreRuntime.1.1.appx"
  if ($src.FullName -ne (Resolve-Path $dst -ErrorAction SilentlyContinue)) {
    Copy-Item $src.FullName $dst -Force
  }
  # Keep a stable copy for later deploys (skip if source already is that file).
  $toolsDep = Join-Path $root "tools\deps\ARM"
  New-Item -ItemType Directory -Force -Path $toolsDep | Out-Null
  $toolsDst = Join-Path $toolsDep "Microsoft.NET.CoreRuntime.1.1.appx"
  if ($src.FullName -ne $toolsDst) {
    Copy-Item $src.FullName $toolsDst -Force
  }
  Write-Host "ARM CoreRuntime dependency staged: $dst"
  return (Get-Item $dst)
}

if ($Build) {
  $msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
  if (-not $msbuild) { throw "MSBuild not found" }
  Write-Host "Building ARM $Configuration..."
  & $msbuild $appProj /p:Configuration=$Configuration /p:Platform=ARM /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /t:Rebuild /v:m /nologo
  if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$appx = Get-ChildItem (Join-Path $root "shell\QQReborn.App\AppPackages") -Recurse -Filter "QQReborn.App_*_ARM*.appx" |
  Where-Object { $_.FullName -notmatch '\\Dependencies\\' } |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw "No ARM appx found. Run with -Build first." }

$packageDir = Split-Path -Parent $appx.FullName
$coreRuntime = Ensure-ArmDependenciesLayout -PackageDir $packageDir

Write-Host "Package: $($appx.FullName)"
Write-Host "Target:  $Ip"
if ($coreRuntime) { Write-Host "Framework: $($coreRuntime.FullName)" }

function Enable-TrustAllCerts {
  Add-Type @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCerts {
  public static void Enable() {
    ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
  }
}
"@
  [TrustAllCerts]::Enable()
}

function Get-DevicePortalSession {
  param([string]$DeviceIp)
  $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $null = Invoke-WebRequest -Uri "https://$DeviceIp/" -WebSession $session -UseBasicParsing -TimeoutSec 20
  $csrf = $null
  if ($session.Cookies) {
    foreach ($c in $session.Cookies.GetCookies("https://$DeviceIp/")) {
      if ($c.Name -eq "CSRF-Token") { $csrf = $c.Value }
    }
  }
  if (-not $csrf) { throw "No CSRF-Token cookie from Device Portal at https://$DeviceIp/" }
  return @{ Session = $session; Csrf = $csrf }
}

function Refresh-Csrf {
  param($State, [string]$DeviceIp)
  $null = Invoke-WebRequest -Uri "https://$DeviceIp/" -WebSession $State.Session -UseBasicParsing -TimeoutSec 20
  foreach ($c in $State.Session.Cookies.GetCookies("https://$DeviceIp/")) {
    if ($c.Name -eq "CSRF-Token") { $State.Csrf = $c.Value }
  }
}

function Install-PackageViaDevicePortal {
  param(
    [string]$DeviceIp,
    [string]$PackagePath,
    [hashtable]$State,
    [string]$Label = "package"
  )

  $pkgName = [IO.Path]::GetFileName($PackagePath)
  Refresh-Csrf -State $State -DeviceIp $DeviceIp
  $installUri = "https://$DeviceIp/api/app/packagemanager/package?package=$([uri]::EscapeDataString($pkgName))"
  Write-Host "Uploading $Label via Device Portal REST (multipart): $pkgName"

  # Device Portal expects multipart/form-data with the package file field, not a raw body.
  $boundary = [guid]::NewGuid().ToString("N")
  $header = "--$boundary`r`nContent-Disposition: form-data; name=`"data`"; filename=`"$pkgName`"`r`nContent-Type: application/octet-stream`r`n`r`n"
  $footer = "`r`n--$boundary--`r`n"
  $headerBytes = [Text.Encoding]::UTF8.GetBytes($header)
  $footerBytes = [Text.Encoding]::UTF8.GetBytes($footer)
  $fileBytes = [IO.File]::ReadAllBytes($PackagePath)
  $body = New-Object byte[] ($headerBytes.Length + $fileBytes.Length + $footerBytes.Length)
  [Array]::Copy($headerBytes, 0, $body, 0, $headerBytes.Length)
  [Array]::Copy($fileBytes, 0, $body, $headerBytes.Length, $fileBytes.Length)
  [Array]::Copy($footerBytes, 0, $body, $headerBytes.Length + $fileBytes.Length, $footerBytes.Length)

  $resp = Invoke-WebRequest -Uri $installUri -Method POST -Body $body `
    -ContentType "multipart/form-data; boundary=$boundary" `
    -WebSession $State.Session -Headers @{ "X-CSRF-Token" = $State.Csrf } -UseBasicParsing -TimeoutSec 300
  Write-Host "Upload status: $($resp.StatusCode) $($resp.Content)"

  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 2
    Refresh-Csrf -State $State -DeviceIp $DeviceIp
    try {
      $st = Invoke-WebRequest -Uri "https://$DeviceIp/api/app/packagemanager/state" -WebSession $State.Session `
        -Headers @{ "X-CSRF-Token" = $State.Csrf } -UseBasicParsing -TimeoutSec 30
      Write-Host "state: $($st.Content)"
      if ($st.Content -match '"Success"\s*:\s*true' -or $st.Content -match '"Code"\s*:\s*0') {
        Write-Host "OK — $Label installed on $DeviceIp"
        return
      }
      if ($st.Content -match 'blocked|failed|error' -and $st.Content -notmatch 'IsRunning"\s*:\s*true') {
        # Dependency already present is fine.
        if ($st.Content -match '0x80073D06|already|registered|installed') {
          Write-Host "OK — $Label already present"
          return
        }
        throw "Install failed: $($st.Content)"
      }
    } catch {
      if ($_.Exception.Message -match 'Install failed') { throw }
      Write-Host "state poll: $($_.Exception.Message)"
    }
  }
  throw "Install did not report success in time for $Label"
}

function Install-ViaDevicePortal {
  param([string]$DeviceIp, [string]$PackagePath, [string]$DependencyPath)

  Enable-TrustAllCerts
  $state = Get-DevicePortalSession -DeviceIp $DeviceIp
  Write-Host "Device Portal CSRF acquired"

  $fullName = "QQReborn.App_0.1.0.0_arm__v51r6sgtx3ez2"

  # Best-effort uninstall of same identity so reinstall is not blocked.
  try {
    Refresh-Csrf -State $state -DeviceIp $DeviceIp
    $null = Invoke-WebRequest -Uri "https://$DeviceIp/api/app/packagemanager/package?package=$([uri]::EscapeDataString($fullName))" `
      -Method DELETE -WebSession $state.Session -Headers @{ "X-CSRF-Token" = $state.Csrf } -UseBasicParsing -TimeoutSec 120
    Write-Host "Uninstalled existing $fullName (or already gone)"
    Start-Sleep -Seconds 2
  } catch {
    Write-Host "Uninstall skip: $($_.Exception.Message)"
  }

  if ($DependencyPath -and (Test-Path $DependencyPath)) {
    try {
      Install-PackageViaDevicePortal -DeviceIp $DeviceIp -PackagePath $DependencyPath -State $state -Label "ARM CoreRuntime"
    } catch {
      Write-Host "CoreRuntime install note: $($_.Exception.Message)"
      Write-Host "Continuing with app package (runtime may already be installed)."
    }
  }

  Install-PackageViaDevicePortal -DeviceIp $DeviceIp -PackagePath $PackagePath -State $state -Label "QQReborn.App"
}

function Install-ViaWinAppDeployCmd {
  param([string]$DeviceIp, [string]$PackagePath, [string]$DependencyPath, [string]$DevicePin)

  $deploy = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x86\WinAppDeployCmd.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x86\WinAppDeployCmd.exe"
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1

  if (-not $deploy) { throw "WinAppDeployCmd.exe not found (install Windows 10 SDK)." }
  Write-Host "Using $deploy"
  if ($DevicePin) { Write-Host "Pin: (provided)" } else { Write-Host "Pin: (none)" }

  if ($DependencyPath -and (Test-Path $DependencyPath)) {
    Write-Host "Installing ARM CoreRuntime first..."
    $depArgs = @("install", "-file", $DependencyPath, "-ip", $DeviceIp)
    if ($DevicePin) { $depArgs += @("-pin", $DevicePin) }
    & $deploy @depArgs
    if ($LASTEXITCODE -ne 0) {
      Write-Host "CoreRuntime install exit=$LASTEXITCODE (may already exist). Continuing..."
    }
  }

  $args = @("install", "-file", $PackagePath, "-ip", $DeviceIp)
  if ($DevicePin) { $args += @("-pin", $DevicePin) }
  & $deploy @args
  if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "FAILED (device often needs pairing PIN, or still missing framework dependency)."
    Write-Host "  1. Phone: Settings > Update & security > For developers"
    Write-Host "     - Developer mode ON"
    Write-Host "     - Device discovery / Device Portal ON"
    Write-Host "     - Note the pairing PIN when prompted"
    Write-Host "  2. Re-run:"
    Write-Host "     powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip $DeviceIp -Pin <PIN>"
    Write-Host "  3. Device Portal REST (no pin if auth disabled):"
    Write-Host "     powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip $DeviceIp -ViaDevicePortal"
    Write-Host "  4. Browser: https://$DeviceIp/  (Apps > install app + Dependencies\ARM\Microsoft.NET.CoreRuntime.1.1.appx)"
    exit $LASTEXITCODE
  }
  Write-Host "OK — package installed on $DeviceIp"
}

$depPath = if ($coreRuntime) { $coreRuntime.FullName } else { $null }

if ($ViaDevicePortal -or -not $Pin) {
  try {
    Install-ViaDevicePortal -DeviceIp $Ip -PackagePath $appx.FullName -DependencyPath $depPath
    Write-Host ""
    Write-Host "If the app still flash-crashes, open Device Portal Apps page and confirm"
    Write-Host "Microsoft.NET.CoreRuntime.1.1 (ARM) is installed, then reinstall the app."
    exit 0
  } catch {
    Write-Host "Device Portal path failed: $($_.Exception.Message)"
    if ($ViaDevicePortal) { throw }
    Write-Host "Falling back to WinAppDeployCmd..."
  }
}

Install-ViaWinAppDeployCmd -DeviceIp $Ip -PackagePath $appx.FullName -DependencyPath $depPath -DevicePin $Pin
