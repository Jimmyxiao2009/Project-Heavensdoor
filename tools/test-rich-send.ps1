# Rich send smoke test: mixed image+text, @mention, file.
# Usage (gateway + NapCat already up):
#   powershell -ExecutionPolicy Bypass -File tools\test-rich-send.ps1 -Password test-pass-123

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8765,
    [string]$Password = "test-pass-123",
    # 私聊测试默认发往大号（小号 2901884390 持号时）
    [string]$FriendConv = "f1913695019",
    [string]$GroupConv = "g235480098",
    [long]$AtUin = 1913695019
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "tools\WsRichSendTest\WsRichSendTest.csproj"
dotnet run --project $proj -- `
    --host $HostName --port $Port --password $Password `
    --friend $FriendConv --group $GroupConv --at-uin $AtUin
exit $LASTEXITCODE
