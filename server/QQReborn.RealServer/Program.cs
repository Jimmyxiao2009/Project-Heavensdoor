using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using QQReborn.RealServer;

// QQ Reborn gateway host:
//   Program.cs     — HTTP + WebSocket accept / auth / health
//   WireRpc.cs     — Shell JSON-RPC dispatch -> ISessionBackend
//   SessionHub.cs  — per-connection NapCat sessions
//   NapCat/        — OneBot adapter (only backend)
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
    using var dispatchLock = new SemaphoreSlim(1, 1);

    async Task SendRawAsync(string text)
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

    ClientConnection conn = null!;
    conn = hub.RegisterConnection(socket, SendRawAsync);
    async Task SendAsync(string text) => await conn!.SendSafeAsync(text);
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
        await dispatchLock.WaitAsync();
        try
        {
            var reply = await WireRpc.HandleAsync(text, conn, hub);
            if (reply != null) await SendAsync(reply);
        }
        catch (Exception ex) { Console.WriteLine("[!] DispatchAsync: " + ex); }
        finally { dispatchLock.Release(); }
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
                if (!authenticated && !WireAuth.SafePasswordEquals(password, accessPassword))
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
Console.WriteLine("  start script: server/tools/start-user-gateway.ps1");
app.Run();
