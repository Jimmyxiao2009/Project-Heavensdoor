using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using QQReborn.RealServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8765");

// Multi-protocol: lagrange (default) | napcat  via QQREBORN_BACKEND or QQReborn:Backend
var backend = BackendFactory.Create(builder.Configuration);
builder.Services.AddSingleton<ISessionBackend>(backend);

var app = builder.Build();
app.UseWebSockets();

var sessions = app.Services.GetRequiredService<ISessionBackend>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"[+] client connected ({context.Connection.RemoteIpAddress}) backend={sessions.BackendId}");

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
    sessions.Broadcast += OnBroadcast;

    // A single slow request must not block every other request on the same connection.
    async Task DispatchAsync(string text)
    {
        try
        {
            var reply = await HandleAsync(text);
            if (reply != null) await SendAsync(reply);
        }
        catch (Exception ex) { Console.WriteLine("[!] DispatchAsync: " + ex); }
    }

    const int MaxMessageBytes = 2 * 1024 * 1024;
    var buffer = new byte[16 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            var oversized = false;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                if (!oversized)
                {
                    if (ms.Length + result.Count > MaxMessageBytes)
                        oversized = true;
                    else
                        ms.Write(buffer, 0, result.Count);
                }
            } while (!result.EndOfMessage);

            if (oversized)
            {
                Console.WriteLine($"[!] client message exceeded {MaxMessageBytes} bytes, closing connection");
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                return;
            }

            var text = Encoding.UTF8.GetString(ms.ToArray());
            RunSafe(DispatchAsync(text));
        }
    }
    catch (WebSocketException) { /* client dropped */ }
    finally
    {
        sessions.Broadcast -= OnBroadcast;
        Console.WriteLine("[-] client disconnected");
    }
});

app.MapGet("/", () =>
    $"QQReborn server running (backend={sessions.BackendId}). Connect WebSocket to ws://<host>:8765/ws");

app.MapGet("/backend", () => Results.Json(new
{
    backend = sessions.BackendId,
    env = Environment.GetEnvironmentVariable("QQREBORN_BACKEND"),
    config = builder.Configuration["QQReborn:Backend"],
}));

// QQ 空间 / 动态 webhook（Lagrange 原生拉取 + 外部注入；NapCat 下仍可注入）
app.MapPost("/webhook/space", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var bodyText = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(bodyText))
        return Results.Json(new { ok = false, reason = "empty-body" }, statusCode: 400);
    try
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(bodyText);
        var result = sessions.IngestSpaceWebhook(node);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine("[!] /webhook/space: " + ex.Message);
        return Results.Json(new { ok = false, reason = ex.Message }, statusCode: 400);
    }
});

app.MapGet("/webhook/space", () => Results.Text(
    "POST JSON space posts here.\n" +
    "Example: {\"author\":\"张三\",\"text\":\"hello\",\"images\":[\"https://...\"]}\n" +
    "Or: {\"items\":[ {...}, {...} ]}\n"));

Console.WriteLine($"QQReborn server listening on http://0.0.0.0:8765  (ws://localhost:8765/ws)");
Console.WriteLine($"  backend: {sessions.BackendId}");
Console.WriteLine("  space webhook: POST http://0.0.0.0:8765/webhook/space");
Console.WriteLine("  switch: env QQREBORN_BACKEND=lagrange|napcat  or  QQReborn:Backend in appsettings");
app.Run();

async Task<string?> HandleAsync(string text)
{
    JsonObject req;
    try { req = (JsonObject)JsonNode.Parse(text)!; }
    catch { return null; }

    static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    var id = S(req, "id") ?? "";
    var type = S(req, "type") ?? "";

    try
    {
        JsonNode? data;
        string? error = null;

        switch (type)
        {
            case "getSelf":
                data = sessions.GetSelf();
                break;
            case "getConversations":
                data = sessions.GetConversations();
                break;
            case "getContacts":
                data = await sessions.GetContactsAsync();
                break;
            case "getMessages":
            {
                static bool? B(JsonObject o, string k)
                    => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
                var localOnly = B(req, "localOnly") == true;
                data = await sessions.GetMessagesAsync(S(req, "conversationId") ?? "", allowCloudBackfill: !localOnly);
                break;
            }
            case "getGroupMembers":
                data = await sessions.GetGroupMembersAsync(S(req, "conversationId") ?? "");
                break;
            case "getFriendRequests":
                data = sessions.GetFriendRequests();
                break;
            case "acceptFriendRequest":
                data = sessions.AcceptFriendRequest((long)N(req, "uin"));
                break;
            case "getUserProfile":
                (data, error) = await sessions.GetUserProfileAsync((long)N(req, "uin"));
                break;
            case "getEarlierMessages":
                (data, error) = await sessions.GetEarlierMessagesAsync(
                    S(req, "conversationId") ?? "", S(req, "beforeId"), (int)N(req, "count"));
                break;
            case "recallMessage":
                data = await sessions.RecallMessageAsync(S(req, "conversationId") ?? "", S(req, "messageId") ?? "");
                break;
            case "quitGroup":
                data = await sessions.QuitGroupAsync(S(req, "conversationId") ?? "");
                break;
            case "nudge":
                (data, error) = await sessions.SendNudgeAsync(S(req, "conversationId") ?? "", (long)N(req, "targetUin"));
                break;
            case "setAvatar":
                (data, error) = await sessions.SetAvatarAsync(S(req, "imageBase64") ?? "");
                break;
            case "getMediaUrl":
                (data, error) = await sessions.GetMediaUrlAsync(S(req, "messageId") ?? "");
                break;
            case "getVoicePlayable":
                (data, error) = await sessions.GetVoicePlayableAsync(S(req, "messageId") ?? "");
                break;
            case "getFileDownloadUrl":
                (data, error) = await sessions.GetFileDownloadUrlAsync(
                    S(req, "conversationId") ?? "", S(req, "fileId") ?? "");
                break;
            case "getGroupNotifications":
                (data, error) = await sessions.GetGroupNotificationsAsync();
                break;
            case "handleGroupNotification":
            {
                static bool? B(JsonObject o, string k)
                    => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
                (data, error) = await sessions.HandleGroupNotificationAsync(
                    (long)N(req, "groupUin"), (ulong)N(req, "sequence"),
                    S(req, "notifType") ?? "join", S(req, "operate") ?? "accept",
                    S(req, "message"), B(req, "isFiltered") == true);
                break;
            }
            case "setGroupReaction":
            {
                var isAdd = true;
                if (req["isAdd"] is JsonValue jv)
                {
                    if (jv.TryGetValue<bool>(out var b)) isAdd = b;
                    else if (jv.TryGetValue<double>(out var d)) isAdd = d != 0;
                    else if (jv.TryGetValue<string>(out var sAdd)) isAdd = !string.Equals(sAdd, "false", StringComparison.OrdinalIgnoreCase);
                }
                (data, error) = await sessions.SetGroupReactionAsync(
                    S(req, "conversationId") ?? "", S(req, "messageId") ?? "",
                    S(req, "code") ?? "", isAdd);
                break;
            }
            case "getMoments":
            case "getSpaceFeed":
                data = sessions.GetSpaceFeed();
                {
                    var moments = data?["moments"] as JsonArray;
                    var empty = moments == null || moments.Count == 0;
                    if (empty)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await sessions.FetchQzoneFeedNativeAsync(); }
                            catch (Exception ex) { Console.WriteLine("[!] background space fetch: " + ex.Message); }
                        });
                    }
                }
                break;
            case "fetchSpaceFeed":
                try { await sessions.FetchQzoneFeedNativeAsync(); }
                catch (Exception ex) { Console.WriteLine("[!] fetchSpaceFeed: " + ex.Message); }
                data = sessions.GetSpaceFeed();
                break;
            case "fetchEarlierSpaceFeed":
                data = await sessions.FetchEarlierSpaceFeedAsync((int)N(req, "num"));
                break;
            case "setSpaceLike":
            {
                var isLiked = false;
                if (req["isLiked"] is JsonValue likeValue)
                {
                    if (likeValue.TryGetValue<bool>(out var b)) isLiked = b;
                    else if (likeValue.TryGetValue<double>(out var d)) isLiked = d != 0;
                }
                data = sessions.SetSpaceLike(S(req, "momentId") ?? "", isLiked);
                break;
            }
            case "send":
                (data, error) = await sessions.SendAsync(
                    S(req, "conversationId") ?? "", S(req, "text") ?? "", S(req, "replyToId"),
                    S(req, "contentType") ?? "Text", S(req, "placeName"), S(req, "address"), S(req, "thumb"),
                    S(req, "imageBase64"), req["imagesBase64"], S(req, "audioBase64"), (int)N(req, "voiceSeconds"),
                    S(req, "fileBase64"), S(req, "fileName"), S(req, "mentions"));
                break;
            case "forward":
                (data, error) = await sessions.ForwardAsync(
                    S(req, "conversationId") ?? "", S(req, "messageId") ?? "");
                break;
            case "configureAccount":
                (data, error) = await sessions.ConfigureAccountAsync(
                    S(req, "signUrl") ?? "",
                    S(req, "signToken"),
                    S(req, "signUin") ?? "");
                break;
            case "setConversationFlags":
            {
                static bool? B(JsonObject o, string k)
                {
                    if (o[k] is not JsonValue v) return null;
                    if (v.TryGetValue<bool>(out var b)) return b;
                    return null;
                }
                data = sessions.SetConversationFlags(
                    S(req, "conversationId") ?? "",
                    B(req, "isPinned"),
                    B(req, "isMuted"));
                break;
            }
            case "markConversationRead":
                data = sessions.MarkConversationRead(S(req, "conversationId") ?? "");
                break;
            case "groupRename":
                (data, error) = await sessions.GroupRenameAsync(S(req, "conversationId") ?? "", S(req, "name") ?? "");
                break;
            case "groupMemberRename":
                (data, error) = await sessions.GroupMemberRenameAsync(S(req, "conversationId") ?? "", (long)N(req, "targetUin"), S(req, "name") ?? "");
                break;
            case "groupSetSpecialTitle":
                (data, error) = await sessions.GroupSetSpecialTitleAsync(S(req, "conversationId") ?? "", (long)N(req, "targetUin"), S(req, "title") ?? "");
                break;
            default:
                data = null;
                break;
        }

        var resp = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = data };
        if (error != null) resp["error"] = error;
        return resp.ToJsonString();
    }
    catch (Exception ex)
    {
        Console.WriteLine("[!] HandleAsync(" + type + "): " + ex);
        var err = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = null, ["error"] = ex.Message };
        return err.ToJsonString();
    }
}
