using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QQReborn.RealServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8765");

// Each Shell connection gets an isolated NapCat-backed session.
builder.Services.AddSingleton<SessionHub>();

var app = builder.Build();
app.UseWebSockets();

// Prefer non-empty values. appsettings.json ships AccessPassword:"" which is NOT null,
// so `config ?? env` would ignore QQREBORN_ACCESS_PASSWORD / QQReborn__AccessPassword
// and leave the gateway open (or fail to apply the steward password).
static string FirstNonEmpty(params string?[] values)
{
    foreach (var value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
    }
    return "";
}

var accessPassword = FirstNonEmpty(
    Environment.GetEnvironmentVariable("QQREBORN_ACCESS_PASSWORD"),
    Environment.GetEnvironmentVariable("QQReborn__AccessPassword"),
    builder.Configuration["QQReborn:AccessPassword"]);

var hub = app.Services.GetRequiredService<SessionHub>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
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
        }
        finally { sendLock.Release(); }
    }

    var conn = hub.RegisterConnection(socket, SendAsync);
    // Empty password = open (dev); non-empty = first frame must be type:auth.
    var authenticated = string.IsNullOrEmpty(accessPassword);
    Console.WriteLine($"[+] client {conn.Id[..8]}… ({context.Connection.RemoteIpAddress}) backend={hub.DefaultBackendId} authRequired={!authenticated}");

    static async void RunSafe(Task t)
    {
        try { await t; }
        catch (Exception ex) { Console.WriteLine("[!] " + ex); }
    }

    async Task DispatchAsync(string text)
    {
        try
        {
            var reply = await HandleAsync(text, conn, hub);
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

            JsonObject? frame = null;
            try { frame = JsonNode.Parse(text) as JsonObject; } catch { /* ignore */ }
            var frameType = frame?["type"]?.GetValue<string>() ?? "";
            var frameId = frame?["id"]?.GetValue<string>() ?? "";

            // Always answer auth when the client sends it (even if password is empty / already open).
            if (string.Equals(frameType, "auth", StringComparison.OrdinalIgnoreCase))
            {
                var password = frame?["password"]?.GetValue<string>() ?? "";
                if (!authenticated && !SafePasswordEquals(password, accessPassword))
                {
                    await SendAsync(new JsonObject
                    {
                        ["id"] = frameId,
                        ["type"] = "result",
                        ["data"] = null,
                        ["error"] = "访问密码错误"
                    }.ToJsonString());
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed", CancellationToken.None);
                    return;
                }

                authenticated = true;
                await SendAsync(new JsonObject
                {
                    ["id"] = frameId,
                    ["type"] = "result",
                    ["data"] = new JsonObject { ["ok"] = true }
                }.ToJsonString());
                continue;
            }

            if (!authenticated)
            {
                await SendAsync(new JsonObject
                {
                    ["id"] = frameId,
                    ["type"] = "result",
                    ["data"] = null,
                    ["error"] = "需要先发送 type:auth 鉴权"
                }.ToJsonString());
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication required", CancellationToken.None);
                return;
            }

            RunSafe(DispatchAsync(text));
        }
    }
    catch (WebSocketException) { /* client dropped */ }
    finally
    {
        hub.UnregisterConnection(conn);
        Console.WriteLine($"[-] client {conn.Id[..8]}… disconnected");
    }
});

var mode = Environment.GetEnvironmentVariable("QQREBORN_MODE")
           ?? builder.Configuration["QQReborn:Mode"]
           ?? "localGateway";

app.MapGet("/", () =>
    $"QQReborn gateway mode={mode} backend={hub.DefaultBackendId}. " +
    "ws://<host>:8765/ws — local NapCat + optional Frp (docs/USER-GATEWAY-OPENFRP.md)");

app.MapGet("/backend", () => Results.Json(new
{
    mode,
    defaultBackend = hub.DefaultBackendId,
    env = Environment.GetEnvironmentVariable("QQREBORN_BACKEND"),
    config = builder.Configuration["QQReborn:Backend"],
    napcatHttp = builder.Configuration["NapCat:HttpBase"],
    accessPasswordConfigured = !string.IsNullOrEmpty(accessPassword),
    docs = new
    {
        gateway = "docs/USER-GATEWAY-SAKURAFRP.md",
        multiTenant = "docs/MULTI-TENANT.md",
        backendSwitch = "docs/BACKEND-SWITCH.md",
    },
}));

app.MapPost("/webhook/space", async (HttpRequest req) =>
{
    // Optional: external scrapers may still POST; primary path is live QZone via NapCat cookies.
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Json(new
    {
        ok = true,
        note = "Primary moments come from NapCat QZone cookies (getActiveFeeds). Webhook is optional.",
        received = body.Length,
    });
});

app.MapGet("/webhook/space", () => Results.Text(
    "QQ 空间: Shell getMoments uses NapCat cookies → QZone getActiveFeeds. Optional POST JSON here for extras.\n"));

Console.WriteLine("QQReborn server on http://0.0.0.0:8765  (ws://localhost:8765/ws)");
Console.WriteLine($"  mode={mode}  backend={hub.DefaultBackendId}");
Console.WriteLine($"  accessPassword={(string.IsNullOrEmpty(accessPassword) ? "open (dev)" : "required")}");
Console.WriteLine("  localGateway: NapCat must be logged in on THIS machine (HTTP/WS localhost only).");
Console.WriteLine("  go outside: Frp map 127.0.0.1:8765 only — docs/USER-GATEWAY-OPENFRP.md");
Console.WriteLine("  start script: tools/start-user-gateway.ps1");
app.Run();

/// <summary>Length-safe constant-time password compare (FixedTimeEquals throws on length mismatch).</summary>
static bool SafePasswordEquals(string provided, string expected)
{
    var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? ""));
    var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? ""));
    return CryptographicOperations.FixedTimeEquals(a, b);
}

async Task<string?> HandleAsync(string text, ClientConnection conn, SessionHub hub)
{
    JsonObject req;
    try { req = (JsonObject)JsonNode.Parse(text)!; }
    catch { return null; }

    static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double N(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    var id = S(req, "id") ?? "";
    var type = S(req, "type") ?? "";

    var sessions = hub.BackendFor(conn);

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
                // If empty, kick a background refresh once.
                if (data?["moments"] is JsonArray ma && ma.Count == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await sessions.FetchQzoneFeedNativeAsync(); }
                        catch (Exception ex) { Console.WriteLine("[!] moments refresh: " + ex.Message); }
                    });
                }
                break;
            case "fetchSpaceFeed":
                try { await sessions.FetchQzoneFeedNativeAsync(); } catch (Exception ex) { Console.WriteLine("[!] fetchSpaceFeed: " + ex.Message); }
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
                if (error == null && conn.Session != null)
                {
                    var resultUin = data?["uin"] is JsonValue jv && jv.TryGetValue<long>(out var lu) ? lu
                        : data?["uin"] is JsonValue jvd && jvd.TryGetValue<double>(out var du) ? (long)du
                        : 0L;
                    if (resultUin <= 0 && long.TryParse(S(req, "signUin"), out var reqUin))
                        resultUin = reqUin;
                    if (resultUin > 0) conn.Session.Uin = resultUin;
                }
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
                data = sessions.MarkConversationRead(
                    S(req, "conversationId") ?? "",
                    S(req, "lastReadAt"));
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
