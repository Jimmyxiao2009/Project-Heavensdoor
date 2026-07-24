param([string]$Password = "test-pass-123")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root "tools\WsFeatureTest\WsFeatureTest.csproj") -- --password $Password
exit $LASTEXITCODE
