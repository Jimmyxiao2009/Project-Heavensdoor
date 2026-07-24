using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

var host = "127.0.0.1";
var port = 8765;
var password = "test-pass-123";
var group = "g235480098";
long preferAt = 0;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--password": password = args[++i]; break;
        case "--group": group = args[++i]; break;
        case "--at-uin": preferAt = long.Parse(args[++i]); break;
    }
}

using var ws = new ClientWebSocket();
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await ws.ConnectAsync(new Uri($"ws://{host}:{port}/ws"), cts.Token);
Console.WriteLine("CONNECTED");

async Task<JsonObject> Rpc(string type, Action<JsonObject>? fill = null)
{
    var id = Guid.NewGuid().ToString("N");
    var body = new JsonObject { ["id"] = id, ["type"] = type };
    fill?.Invoke(body);
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
        var obj = JsonNode.Parse(text) as JsonObject ?? throw new Exception("bad json");
        if ((string?)obj["type"] != "result") continue;
        if ((string?)obj["id"] != id) continue;
        Console.WriteLine("<< " + (text.Length > 500 ? text[..500] + "..." : text));
        return obj;
    }
    throw new TimeoutException(type);
}

var auth = await Rpc("auth", o => o["password"] = password);
if (auth["error"] != null) { Console.WriteLine("AUTH_FAIL " + auth["error"]); return 3; }
Console.WriteLine("AUTH_OK");

var cfg = await Rpc("configureAccount", o => { o["signUrl"] = ""; o["signToken"] = ""; o["signUin"] = ""; });
if (cfg["error"] != null) { Console.WriteLine("CFG_FAIL " + cfg["error"]); return 5; }
Console.WriteLine("CFG_OK uin=" + cfg["data"]?["uin"]);

var mem = await Rpc("getGroupMembers", o => o["conversationId"] = group);
if (mem["error"] != null) { Console.WriteLine("MEMBERS_FAIL " + mem["error"]); return 6; }
var arr = mem["data"] as JsonArray;
if (arr == null || arr.Count == 0) { Console.WriteLine("MEMBERS_EMPTY"); return 6; }
Console.WriteLine("MEMBERS_OK count=" + arr.Count);
foreach (var n in arr.Take(8))
{
    if (n is not JsonObject o) continue;
    Console.WriteLine("  member uin=" + o["uin"] + " name=" + o["name"] + " role=" + o["role"]);
}

// pick at target
long atUin = preferAt;
string atName = "全体成员";
if (atUin <= 0)
{
    foreach (var n in arr)
    {
        if (n is not JsonObject o) continue;
        var u = o["uin"] is JsonValue jv && jv.TryGetValue<long>(out var lu) ? lu
            : o["uin"] is JsonValue jd && jd.TryGetValue<double>(out var d) ? (long)d : 0;
        var self = cfg["data"]?["uin"] is JsonValue su && su.TryGetValue<long>(out var s) ? s
            : cfg["data"]?["uin"] is JsonValue sd && sd.TryGetValue<double>(out var ds) ? (long)ds : 0;
        if (u > 0 && u != self) { atUin = u; atName = o["name"]?.ToString()?.Trim('"') ?? u.ToString(); break; }
    }
}
if (atUin <= 0 && arr[0] is JsonObject first)
{
    atUin = first["uin"] is JsonValue jv && jv.TryGetValue<long>(out var lu) ? lu : 0;
    atName = first["name"]?.ToString()?.Trim('"') ?? atUin.ToString();
}

var display = atName.StartsWith("@") ? atName : "@" + atName;
var mentions = new JsonArray { new JsonObject { ["uin"] = atUin, ["display"] = display } };
var send = await Rpc("send", o =>
{
    o["conversationId"] = group;
    o["contentType"] = "Text";
    o["text"] = display + " 成员列表联调 " + DateTime.Now.ToString("HH:mm:ss");
    o["mentions"] = mentions.ToJsonString();
});
if (send["error"] != null) { Console.WriteLine("AT_FAIL " + send["error"]); return 7; }
Console.WriteLine("AT_OK id=" + send["data"]?["id"] + " text=" + send["data"]?["text"] + " els=" + send["data"]?["elements"]);
Console.WriteLine("GROUP_CHAT_OK");
return 0;
