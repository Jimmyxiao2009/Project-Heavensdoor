using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

// Live tests: members, forward, markRead, groupNotifications, reaction (best-effort), file url.
var host = "127.0.0.1";
var port = 8765;
var password = "test-pass-123";
// 私聊测试默认发往大号 1913695019
var friend = "f1913695019";
var group = "g235480098";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--password": password = args[++i]; break;
        case "--friend": friend = args[++i]; break;
        case "--group": group = args[++i]; break;
    }
}

using var ws = new ClientWebSocket();
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await ws.ConnectAsync(new Uri($"ws://{host}:{port}/ws"), cts.Token);
Console.WriteLine("CONNECTED");

async Task<JsonObject> Rpc(string type, Action<JsonObject>? fill = null)
{
    var id = Guid.NewGuid().ToString("N");
    var body = new JsonObject { ["id"] = id, ["type"] = type };
    fill?.Invoke(body);
    await ws.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, cts.Token);
    var buf = new byte[1024 * 1024];
    for (var n = 0; n < 100; n++)
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
        var obj = JsonNode.Parse(text) as JsonObject ?? throw new Exception("bad");
        if ((string?)obj["type"] != "result") { Console.WriteLine("..push " + (obj["type"])); continue; }
        if ((string?)obj["id"] != id) continue;
        Console.WriteLine("<< " + type + " " + (text.Length > 280 ? text[..280] + "..." : text));
        return obj;
    }
    throw new TimeoutException(type);
}

var fails = 0;
void Ok(string name, bool pass, string? detail = null)
{
    if (pass) Console.WriteLine("PASS " + name + (detail != null ? " " + detail : ""));
    else { Console.WriteLine("FAIL " + name + " " + detail); fails++; }
}

var auth = await Rpc("auth", o => o["password"] = password);
Ok("auth", auth["error"] == null, (string?)auth["error"]);
var cfg = await Rpc("configureAccount", o => { o["signUrl"] = ""; o["signToken"] = ""; o["signUin"] = ""; });
Ok("configure", cfg["error"] == null, (string?)cfg["error"]);

var mem = await Rpc("getGroupMembers", o => o["conversationId"] = group);
Ok("getGroupMembers", mem["error"] == null && mem["data"] is JsonArray a && a.Count > 0, "count=" + (mem["data"] as JsonArray)?.Count);

var friends = await Rpc("getFriendRequests");
Ok("getFriendRequests", friends["error"] == null, "count=" + (friends["data"] as JsonArray)?.Count);

var notif = await Rpc("getGroupNotifications");
var notifCount = (notif["data"]?["notifications"] as JsonArray)?.Count
    ?? (notif["data"] as JsonArray)?.Count
    ?? 0;
Ok("getGroupNotifications", notif["error"] == null, "count=" + notifCount);

// send then forward
var send = await Rpc("send", o =>
{
    o["conversationId"] = friend;
    o["contentType"] = "Text";
    o["text"] = "feature-src " + DateTime.Now.ToString("HH:mm:ss");
});
Ok("send", send["error"] == null, (string?)send["data"]?["id"]);
var srcId = (string?)send["data"]?["id"] ?? "";

var fwd = await Rpc("forward", o =>
{
    o["conversationId"] = friend;
    o["messageId"] = srcId;
});
Ok("forward", fwd["error"] == null, (string?)fwd["error"] ?? (string?)fwd["data"]?["id"]);

var read = await Rpc("markConversationRead", o => o["conversationId"] = friend);
Ok("markRead", read["error"] == null, (string?)read["error"]);

// reaction best-effort on the message we just sent
var react = await Rpc("setGroupReaction", o =>
{
    o["conversationId"] = group;
    o["messageId"] = srcId;
    o["code"] = "128077";
    o["isAdd"] = true;
});
// may fail for private-msg ids / emoji rules — report but don't hard-fail suite if napcat rejects
if (react["error"] != null) Console.WriteLine("SOFT setGroupReaction " + react["error"]);
else Ok("setGroupReaction", true);

// file download API shape (empty id should error cleanly)
var file = await Rpc("getFileDownloadUrl", o =>
{
    o["conversationId"] = friend;
    o["fileId"] = "";
});
Ok("getFileDownloadUrl empty", file["error"] != null, (string?)file["error"]);

// profile
var prof = await Rpc("getUserProfile", o => o["uin"] = 1913695019);
Ok("getUserProfile", prof["error"] == null && prof["data"] != null, (string?)prof["data"]?["nickname"]);

// nudge
var nudge = await Rpc("nudge", o =>
{
    o["conversationId"] = friend;
    o["targetUin"] = 1913695019;
});
if (nudge["error"] != null) Console.WriteLine("SOFT nudge " + nudge["error"]);
else Ok("nudge", true);

Console.WriteLine(fails == 0 ? "ALL_FEATURES_OK" : "FEATURE_FAILS=" + fails);
return fails == 0 ? 0 : 1;
