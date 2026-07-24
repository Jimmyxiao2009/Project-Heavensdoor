# End-to-end: getGroupMembers + @mention send via gateway wire.
param(
    [string]$Password = "test-pass-123",
    [string]$Group = "g235480098",
    [long]$AtUin = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "tools\WsGroupChatTest\WsGroupChatTest.csproj"
dotnet run --project $proj -- --password $Password --group $Group --at-uin $AtUin
exit $LASTEXITCODE
