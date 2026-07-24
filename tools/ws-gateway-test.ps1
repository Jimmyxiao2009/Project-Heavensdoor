# Exercise RealServer wire protocol: auth gate + configureAccount + list + send.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\ws-gateway-test.ps1 -Password secret
#   powershell -ExecutionPolicy Bypass -File tools\ws-gateway-test.ps1 -Password secret -SkipSend
#   powershell -ExecutionPolicy Bypass -File tools\ws-gateway-test.ps1 -Password secret -ConversationId f123456 -Text "hi"

param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 8765,
    [string]$Password = "",
    [string]$ConversationId = "",
    [string]$Text = "",
    [switch]$SkipSend,
    [switch]$ExpectAuthFail
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Send-WsJson([System.Net.WebSockets.ClientWebSocket]$ws, [hashtable]$obj) {
    $json = ($obj | ConvertTo-Json -Compress -Depth 8)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList @(,$bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    return $json
}

function Receive-WsJson([System.Net.WebSockets.ClientWebSocket]$ws, [int]$timeoutMs = 15000) {
    $buffer = New-Object byte[] 65536
    $ms = New-Object System.IO.MemoryStream
    $cts = New-Object Threading.CancellationTokenSource $timeoutMs
    do {
        $seg = New-Object System.ArraySegment[byte] -ArgumentList @(,$buffer)
        $result = $ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            return $null
        }
        $ms.Write($buffer, 0, $result.Count)
    } while (-not $result.EndOfMessage)
    $text = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    return $text
}

$uri = "ws://${HostName}:${Port}/ws"
Write-Host "Connecting $uri ..."
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
try {
    $ws.ConnectAsync([Uri]$uri, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
} catch {
    Write-Host "CONNECT_FAIL: $_"
    exit 2
}
Write-Host "CONNECTED"

# 1) Auth
$authId = [guid]::NewGuid().ToString("N")
$authReq = @{ id = $authId; type = "auth"; password = $Password }
Write-Host ">> auth"
Send-WsJson $ws $authReq | Out-Null
$authResp = Receive-WsJson $ws
Write-Host "<< $authResp"
if (-not $authResp) {
    Write-Host "AUTH_FAIL: connection closed with no response"
    exit 3
}
$authObj = $authResp | ConvertFrom-Json
if ($ExpectAuthFail) {
    if ($authObj.error) {
        Write-Host "AUTH_REJECTED_OK: $($authObj.error)"
        try { $ws.Dispose() } catch {}
        exit 0
    }
    Write-Host "AUTH_UNEXPECTED_OK (expected fail)"
    try { $ws.Dispose() } catch {}
    exit 4
}
if ($authObj.error) {
    Write-Host "AUTH_FAIL: $($authObj.error)"
    try { $ws.Dispose() } catch {}
    exit 3
}
Write-Host "AUTH_OK"

# 2) configureAccount (bind NapCat login)
$cfgId = [guid]::NewGuid().ToString("N")
Write-Host ">> configureAccount"
Send-WsJson $ws @{ id = $cfgId; type = "configureAccount"; signUrl = ""; signToken = ""; signUin = "" } | Out-Null
$cfgResp = Receive-WsJson $ws 30000
Write-Host "<< $cfgResp"
$cfgObj = $cfgResp | ConvertFrom-Json
if ($cfgObj.error) {
    Write-Host "CONFIGURE_FAIL: $($cfgObj.error)"
    try { $ws.Dispose() } catch {}
    exit 5
}
Write-Host "CONFIGURE_OK uin=$($cfgObj.data.uin) nick=$($cfgObj.data.nickname)"

# 3) conversations
$convIdReq = [guid]::NewGuid().ToString("N")
Write-Host ">> getConversations"
Send-WsJson $ws @{ id = $convIdReq; type = "getConversations" } | Out-Null
$convResp = Receive-WsJson $ws
Write-Host "<< $convResp"
$convObj = $convResp | ConvertFrom-Json
if ($convObj.error) {
    Write-Host "LIST_FAIL: $($convObj.error)"
    try { $ws.Dispose() } catch {}
    exit 6
}
$convs = @($convObj.data)
Write-Host "LIST_OK count=$($convs.Count)"

# 4) send
if (-not $SkipSend) {
    $target = $ConversationId
    if (-not $target -and $convs.Count -gt 0) {
        $target = $convs[0].id
    }
    if (-not $target) {
        Write-Host "SEND_SKIP: no conversation id"
    } else {
        if (-not $Text) { $Text = "QQReborn gateway test " + (Get-Date -Format "HH:mm:ss") }
        $sendId = [guid]::NewGuid().ToString("N")
        Write-Host ">> send to $target : $Text"
        Send-WsJson $ws @{
            id = $sendId
            type = "send"
            conversationId = $target
            text = $Text
            contentType = "Text"
        } | Out-Null
        $sendResp = Receive-WsJson $ws 30000
        Write-Host "<< $sendResp"
        $sendObj = $sendResp | ConvertFrom-Json
        if ($sendObj.error) {
            Write-Host "SEND_FAIL: $($sendObj.error)"
            try { $ws.Dispose() } catch {}
            exit 7
        }
        Write-Host "SEND_OK id=$($sendObj.data.id) napcatMessageId=$($sendObj.data.napcatMessageId)"
    }
}

try {
    $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "bye", [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
} catch {}
$ws.Dispose()
Write-Host "DONE"
exit 0
