using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using QQReborn.FakeServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8765");
builder.Services.AddSingleton<ChatState>();

var app = builder.Build();
app.UseWebSockets();

var state = app.Services.GetRequiredService<ChatState>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"[+] client connected ({context.Connection.RemoteIpAddress})");

    using var sendLock = new SemaphoreSlim(1, 1);

    async Task SendAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // Client dropped between the state check and the send; swallow so a dead
            // client never escalates into an unobserved task exception.
        }
        finally { sendLock.Release(); }
    }

    // Run a fire-and-forget task with its exceptions observed/logged instead of lost.
    static async void RunSafe(Task t)
    {
        try { await t; }
        catch (Exception ex) { Console.WriteLine("[!] " + ex); }
    }

    void OnBroadcast(string frame) => RunSafe(SendAsync(frame));
    state.Broadcast += OnBroadcast;

    var buffer = new byte[16 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            var reply = Handle(text);
            if (reply != null) await SendAsync(reply);
        }
    }
    catch (WebSocketException) { /* client dropped */ }
    finally
    {
        state.Broadcast -= OnBroadcast;
        Console.WriteLine("[-] client disconnected");
    }
});

app.MapGet("/", () => "QQReborn fake server is running. Connect a WebSocket to ws://<host>:8765/ws");

Console.WriteLine("QQReborn fake server listening on http://0.0.0.0:8765  (ws://localhost:8765/ws)");
app.Run();

string? Handle(string text)
{
    JsonObject req;
    try { req = (JsonObject)JsonNode.Parse(text)!; }
    catch { return null; }

    // Null-tolerant accessors: a JSON null node (vs. an omitted key) or a wrong type must
    // not throw and tear down the socket. Returns null/0 instead.
    static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    static bool? B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    var id = S(req, "id") ?? "";
    var type = S(req, "type") ?? "";

    try
    {
        // getMediaUrl is the one case that needs to report a wire-level "error" instead of a
        // data payload (demo backend has no real media CDN behind it -- see ChatState.GetMediaUrl),
        // so it's special-cased ahead of the data-only switch below rather than reshaping every
        // other case into a (data, error) tuple for one outlier.
        if (type == "getMediaUrl")
        {
            var errResp = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = null, ["error"] = "no-media" };
            return errResp.ToJsonString();
        }

        JsonNode? data = type switch
        {
            "getSelf" => state.GetSelf(),
            "getConversations" => state.GetConversations(),
            "getContacts" => state.GetContacts(),
            "getMessages" => state.GetMessages(S(req, "conversationId") ?? ""),
            "getGroupMembers" => state.GetGroupMembers(S(req, "conversationId") ?? ""),
            "getFriendRequests" => state.GetFriendRequests(),
            "acceptFriendRequest" => state.AcceptFriendRequest((long)N(req, "uin")),
            "getUserProfile" => state.GetUserProfile((long)N(req, "uin")),
            "getEarlierMessages" => state.GetEarlierMessages(
                S(req, "conversationId") ?? "", S(req, "beforeId"), (int)N(req, "count")),
            "recallMessage" => state.RecallMessage(S(req, "conversationId") ?? "", S(req, "messageId") ?? ""),
            "quitGroup" => state.QuitGroup(S(req, "conversationId") ?? ""),
            "nudge" => state.SendNudge(S(req, "conversationId") ?? "", (long)N(req, "targetUin")),
            "setAvatar" => state.SetAvatar(S(req, "imageBase64") ?? ""),
            "setConversationFlags" => state.SetConversationFlags(
                S(req, "conversationId") ?? "", B(req, "isPinned"), B(req, "isMuted")),
            "send" => state.Send(
                S(req, "conversationId") ?? "",
                S(req, "contentType") ?? "Text",
                S(req, "text"),
                S(req, "imagePath"),
                S(req, "audioPath"),
                (int)N(req, "voiceSeconds"),
                S(req, "placeName"),
                S(req, "address"),
                S(req, "thumb"),
                S(req, "replyToId"),
                S(req, "imageBase64")),
            _ => null,
        };

        var resp = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = data };
        return resp.ToJsonString();
    }
    catch (Exception ex)
    {
        Console.WriteLine("[!] Handle(" + type + "): " + ex);
        // Same error envelope as QQReborn.RealServer: the client fails the pending request
        // with this message instead of choking on JsonObject.Parse("null").
        var err = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = null, ["error"] = ex.Message };
        return err.ToJsonString();
    }
}
