using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// Real wire-protocol client against a live RealServer (not a mock of gateway logic).
// Usage:
//   dotnet run --project tools/WsGatewayTest -- --password SECRET
//   dotnet run --project tools/WsGatewayTest -- --password wrong --expect-auth-fail
//   dotnet run --project tools/WsGatewayTest -- --password SECRET --send --text "hi"

static int Usage()
{
    Console.WriteLine("WsGatewayTest --host 127.0.0.1 --port 8765 --password PWD [--expect-auth-fail] [--send] [--conversation f123] [--text msg]");
    return 2;
}

var host = "127.0.0.1";
var port = 8765;
var password = "";
var expectAuthFail = false;
var doSend = false;
var conversationId = "";
var text = "QQReborn gateway test " + DateTime.Now.ToString("HH:mm:ss");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--password": password = args[++i]; break;
        case "--expect-auth-fail": expectAuthFail = true; break;
        case "--send": doSend = true; break;
        case "--conversation": conversationId = args[++i]; break;
        case "--text": text = args[++i]; break;
        case "--help": return Usage();
    }
}

var uri = new Uri($"ws://{host}:{port}/ws");
Console.WriteLine("Connecting " + uri);
using var ws = new ClientWebSocket();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await ws.ConnectAsync(uri, cts.Token);
Console.WriteLine("CONNECTED");

async Task<JsonDocument> ReceiveResultAsync(string expectId)
{
    var buf = new byte[256 * 1024];
    // Skip push frames (loginStatus, messageReceived, …) until matching result id.
    for (var attempt = 0; attempt < 50; attempt++)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new Exception("socket closed by server");
            ms.Write(buf, 0, result.Count);
        } while (!result.EndOfMessage);

        var resp = Encoding.UTF8.GetString(ms.ToArray());
        Console.WriteLine("<< " + resp);
        using var probe = JsonDocument.Parse(resp);
        var root = probe.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
            continue;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (!string.IsNullOrEmpty(expectId) && id != expectId)
            continue;
        return JsonDocument.Parse(resp);
    }
    throw new TimeoutException("no matching result frame for id=" + expectId);
}

async Task<JsonDocument> RequestAsync(object body, string id)
{
    var json = JsonSerializer.Serialize(body);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    return await ReceiveResultAsync(id);
}

// Auth
{
    var id = Guid.NewGuid().ToString("N");
    Console.WriteLine(">> auth");
    using var auth = await RequestAsync(new { id, type = "auth", password }, id);
    var root = auth.RootElement;
    var err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    if (expectAuthFail)
    {
        if (!string.IsNullOrEmpty(err))
        {
            Console.WriteLine("AUTH_REJECTED_OK " + err);
            return 0;
        }
        Console.WriteLine("AUTH_UNEXPECTED_OK");
        return 4;
    }
    if (!string.IsNullOrEmpty(err))
    {
        Console.WriteLine("AUTH_FAIL " + err);
        return 3;
    }
    Console.WriteLine("AUTH_OK");
}

// configureAccount
{
    var id = Guid.NewGuid().ToString("N");
    Console.WriteLine(">> configureAccount");
    using var cfg = await RequestAsync(new { id, type = "configureAccount", signUrl = "", signToken = "", signUin = "" }, id);
    var err = cfg.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    if (!string.IsNullOrEmpty(err))
    {
        Console.WriteLine("CONFIGURE_FAIL " + err);
        return doSend ? 5 : 0;
    }
    var data = cfg.RootElement.GetProperty("data");
    Console.WriteLine("CONFIGURE_OK uin=" + data.GetProperty("uin") + " nick=" + data.GetProperty("nickname"));
}

// list
string? firstConv = null;
{
    var id = Guid.NewGuid().ToString("N");
    Console.WriteLine(">> getConversations");
    using var list = await RequestAsync(new { id, type = "getConversations" }, id);
    var err = list.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    if (!string.IsNullOrEmpty(err))
    {
        Console.WriteLine("LIST_FAIL " + err);
        return 6;
    }
    if (list.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
    {
        firstConv = data[0].GetProperty("id").GetString();
        Console.WriteLine("LIST_OK count=" + data.GetArrayLength() + " first=" + firstConv);
    }
    else
    {
        Console.WriteLine("LIST_OK count=0");
    }
}

if (doSend)
{
    var target = string.IsNullOrEmpty(conversationId) ? firstConv : conversationId;
    if (string.IsNullOrEmpty(target))
    {
        Console.WriteLine("SEND_FAIL no conversation");
        return 7;
    }
    var id = Guid.NewGuid().ToString("N");
    Console.WriteLine(">> send " + target + " : " + text);
    using var send = await RequestAsync(new
    {
        id,
        type = "send",
        conversationId = target,
        text,
        contentType = "Text",
    }, id);
    var err = send.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    if (!string.IsNullOrEmpty(err))
    {
        Console.WriteLine("SEND_FAIL " + err);
        return 7;
    }
    var data = send.RootElement.GetProperty("data");
    Console.WriteLine("SEND_OK id=" + data.GetProperty("id"));
}

Console.WriteLine("DONE");
return 0;
