using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Live wire tests for Mixed / @ / File against RealServer (not a mock).

var host = "127.0.0.1";
var port = 8765;
var password = "test-pass-123";
// 私聊测试默认发往大号 1913695019（小号 NapCat 持号）
var friend = "f1913695019";
var group = "g235480098";
long atUin = 1913695019;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--password": password = args[++i]; break;
        case "--friend": friend = args[++i]; break;
        case "--group": group = args[++i]; break;
        case "--at-uin": atUin = long.Parse(args[++i]); break;
    }
}

using var ws = new ClientWebSocket();
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await ws.ConnectAsync(new Uri($"ws://{host}:{port}/ws"), cts.Token);
Console.WriteLine("CONNECTED");

async Task<JsonObject> RpcAsync(JsonObject body)
{
    var id = body["id"]!.GetValue<string>();
    var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    var buf = new byte[512 * 1024];
    for (var n = 0; n < 80; n++)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) throw new Exception("closed");
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        Console.WriteLine("<< " + (text.Length > 400 ? text[..400] + "..." : text));
        var obj = JsonNode.Parse(text) as JsonObject
                  ?? throw new Exception("bad json");
        if ((string?)obj["type"] != "result") continue;
        if ((string?)obj["id"] != id) continue;
        return obj;
    }
    throw new TimeoutException("no result for " + id);
}

JsonObject Req(string type, Action<JsonObject> fill)
{
    var id = Guid.NewGuid().ToString("N");
    var o = new JsonObject { ["id"] = id, ["type"] = type };
    fill(o);
    return o;
}

// 1px PNG
var png = Convert.ToBase64String(Convert.FromHexString(
    "89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C4890000000A49444154789C63000100000500010D0A2DB40000000049454E44AE426082"));

// auth
{
    var id = Guid.NewGuid().ToString("N");
    Console.WriteLine(">> auth");
    var resp = await RpcAsync(new JsonObject { ["id"] = id, ["type"] = "auth", ["password"] = password });
    if (resp["error"] != null) { Console.WriteLine("AUTH_FAIL " + resp["error"]); return 3; }
    Console.WriteLine("AUTH_OK");
}

// configure
{
    Console.WriteLine(">> configureAccount");
    var resp = await RpcAsync(Req("configureAccount", o =>
    {
        o["signUrl"] = "";
        o["signToken"] = "";
        o["signUin"] = "";
    }));
    if (resp["error"] != null) { Console.WriteLine("CFG_FAIL " + resp["error"]); return 5; }
    Console.WriteLine("CFG_OK uin=" + resp["data"]?["uin"]);
}

var fails = 0;

// Mixed: text + image to friend
{
    Console.WriteLine(">> MIXED friend");
    var resp = await RpcAsync(Req("send", o =>
    {
        o["conversationId"] = friend;
        o["contentType"] = "Mixed";
        o["text"] = "图文混排测试 " + DateTime.Now.ToString("HH:mm:ss");
        o["imageBase64"] = png;
        o["imagesBase64"] = new JsonArray(png);
    }));
    if (resp["error"] != null) { Console.WriteLine("MIXED_FAIL " + resp["error"]); fails++; }
    else
    {
        var ct = (string?)resp["data"]?["contentType"];
        var id = (string?)resp["data"]?["id"];
        Console.WriteLine("MIXED_OK id=" + id + " contentType=" + ct);
        if (string.IsNullOrEmpty(id)) fails++;
    }
}

// @ mention in group
{
    Console.WriteLine(">> AT group");
    var mentions = new JsonArray
    {
        new JsonObject { ["uin"] = atUin, ["display"] = "@小旭" },
    };
    var resp = await RpcAsync(Req("send", o =>
    {
        o["conversationId"] = group;
        o["contentType"] = "Text";
        o["text"] = "@小旭 网关@测试 " + DateTime.Now.ToString("HH:mm:ss");
        o["mentions"] = mentions.ToJsonString();
    }));
    if (resp["error"] != null) { Console.WriteLine("AT_FAIL " + resp["error"]); fails++; }
    else
    {
        Console.WriteLine("AT_OK id=" + resp["data"]?["id"] + " text=" + resp["data"]?["text"]);
        var els = resp["data"]?["elements"]?.ToJsonString() ?? "";
        if (!els.Contains("Mention", StringComparison.OrdinalIgnoreCase)
            && !els.Contains("at", StringComparison.OrdinalIgnoreCase)
            && !(resp["data"]?["text"]?.ToString()?.Contains('@') ?? false))
            Console.WriteLine("AT_WARN elements may lack Mention tag: " + els);
    }
}

// File to friend (small text payload)
{
    Console.WriteLine(">> FILE friend");
    var payload = Encoding.UTF8.GetBytes("QQReborn file test " + DateTime.UtcNow.ToString("o") + "\n");
    var resp = await RpcAsync(Req("send", o =>
    {
        o["conversationId"] = friend;
        o["contentType"] = "File";
        o["fileBase64"] = Convert.ToBase64String(payload);
        o["fileName"] = "qqreborn-test.txt";
        o["text"] = "";
    }));
    if (resp["error"] != null) { Console.WriteLine("FILE_FAIL " + resp["error"]); fails++; }
    else
    {
        Console.WriteLine("FILE_OK id=" + resp["data"]?["id"] + " text=" + resp["data"]?["text"]
                          + " contentType=" + resp["data"]?["contentType"]);
        var t = (string?)resp["data"]?["text"] ?? "";
        if (t.Contains("待完善", StringComparison.Ordinal)) { Console.WriteLine("FILE_STUB"); fails++; }
    }
}

// Mixed + at in group (text+image+at)
{
    Console.WriteLine(">> MIXED_AT group");
    var mentions = new JsonArray
    {
        new JsonObject { ["uin"] = atUin, ["display"] = "@小旭" },
    };
    var resp = await RpcAsync(Req("send", o =>
    {
        o["conversationId"] = group;
        o["contentType"] = "Mixed";
        o["text"] = "@小旭 图文@ " + DateTime.Now.ToString("HH:mm:ss");
        o["mentions"] = mentions.ToJsonString();
        o["imageBase64"] = png;
        o["imagesBase64"] = new JsonArray(png);
    }));
    if (resp["error"] != null) { Console.WriteLine("MIXED_AT_FAIL " + resp["error"]); fails++; }
    else Console.WriteLine("MIXED_AT_OK id=" + resp["data"]?["id"]);
}

Console.WriteLine(fails == 0 ? "ALL_RICH_OK" : "RICH_FAILS=" + fails);
return fails == 0 ? 0 : 7;
