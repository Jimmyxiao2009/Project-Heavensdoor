using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// =============================================================================
// LocalSignProxy — stable local front for QQ energy/sec-sign.
//
// Why: the public signer (sign.lagrangecore.org) returns HTTP 401 under concurrent
// load. RealServer used to stampede it while backfilling chat history, then every
// MessageSvc.PbSendMsg failed and "send to anyone" looked broken.
//
// This process:
//   1. Listens on http://127.0.0.1:18488  (same /api/sign/sec-sign shape as upstream)
//   2. Serializes all sign requests through one gate
//   3. Forwards to UpstreamUrl with UpstreamToken (or the caller's Authorization)
//   4. Retries transient 401/429/5xx
//   5. Records command names for protocol research (pin/mute cloud, etc.)
//
// App settings: enable "自建签名服务器", URL = http://127.0.0.1:18488
// Token can be empty in the App (proxy injects UpstreamToken), or pass-through.
//
// This is NOT a reverse-engineered local energy engine — it still needs a working
// upstream (public or your own). When you have a true local qsign, point
// SignProxy:UpstreamUrl at it instead.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:18488");

var upstreamUrl = (builder.Configuration["SignProxy:UpstreamUrl"] ?? "https://sign.lagrangecore.org").TrimEnd('/');
var upstreamToken = builder.Configuration["SignProxy:UpstreamToken"] ?? "";
var maxRetries = int.TryParse(builder.Configuration["SignProxy:MaxRetries"], out var mr) ? Math.Clamp(mr, 1, 8) : 4;
var retryDelayMs = int.TryParse(builder.Configuration["SignProxy:RetryDelayMs"], out var rd) ? Math.Clamp(rd, 50, 5000) : 400;
var timeoutSec = int.TryParse(builder.Configuration["SignProxy:RequestTimeoutSeconds"], out var ts) ? Math.Clamp(ts, 5, 120) : 30;

var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
};
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
// One-at-a-time to the upstream. Parallel energy signs are what trigger 401s.
var gate = new SemaphoreSlim(1, 1);
var stats = new SignStats();

// Prefer tools/runtime_logs when launched from repo; otherwise next to the executable.
string cmdLogPath;
try
{
    var repoLogDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runtime_logs"));
    cmdLogPath = Directory.Exists(repoLogDir)
        ? Path.Combine(repoLogDir, "sign-commands.jsonl")
        : Path.Combine(AppContext.BaseDirectory, "sign-commands.jsonl");
}
catch
{
    cmdLogPath = Path.Combine(AppContext.BaseDirectory, "sign-commands.jsonl");
}
var cmdLog = new CommandLog(cmdLogPath);

var app = builder.Build();

app.MapGet("/", () => Results.Text(
    "LocalSignProxy is running.\n" +
    $"Upstream: {upstreamUrl}\n" +
    $"Token: {(string.IsNullOrWhiteSpace(upstreamToken) ? "(none — callers must send Authorization)" : "configured")}\n" +
    "POST /api/sign/sec-sign  (same body as Lagrange TokenSignProvider)\n" +
    "GET  /health\n" +
    "GET  /stats\n" +
    "GET  /commands\n"));

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    upstream = upstreamUrl,
    hasToken = !string.IsNullOrWhiteSpace(upstreamToken),
    stats.Total,
    stats.Success,
    stats.Failed,
    stats.Retried,
}));

app.MapGet("/stats", () => Results.Json(stats.Snapshot()));

// Research helper for cloud pin/mute (and general protocol work): recent signed commands.
app.MapGet("/commands", () => Results.Json(cmdLog.Snapshot()));

app.MapPost("/api/sign/sec-sign", async (HttpRequest req) =>
{
    string body;
    using (var reader = new StreamReader(req.Body, Encoding.UTF8))
        body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
        return Results.Json(new { code = -1, message = "empty body" }, statusCode: 400);

    // Always use configured UpstreamToken (caller's Authorization is ignored).
    string? bearer = null;
    if (!string.IsNullOrWhiteSpace(upstreamToken))
    {
        bearer = upstreamToken;
    }

    if (string.IsNullOrWhiteSpace(bearer))
    {
        return Results.Json(new
        {
            code = -1,
            message = "no token: set SignProxy:UpstreamToken in appsettings.json or send Authorization: Bearer <token>"
        }, statusCode: 401);
    }

    // Light validation / logging (never log full body hex — too large).
    string cmd = "?";
    long bodyLen = body.Length;
    try
    {
        var node = JsonNode.Parse(body) as JsonObject;
        cmd = node?["command"]?.GetValue<string>() ?? "?";
    }
    catch { /* ignore */ }

    cmdLog.Record(cmd, bodyLen);

    await gate.WaitAsync();
    try
    {
        stats.IncrementTotal();
        HttpResponseMessage? last = null;
        string? lastText = null;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            last?.Dispose();
            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"{upstreamUrl}/api/sign/sec-sign");
            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            upstreamReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

            try
            {
                last = await http.SendAsync(upstreamReq);
                lastText = await last.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[proxy] {cmd} attempt {attempt}/{maxRetries} transport error: {ex.Message}");
                if (attempt == maxRetries)
                {
                    stats.IncrementFailed();
                    return Results.Json(new { code = -1, message = "upstream transport: " + ex.Message }, statusCode: 502);
                }
                stats.IncrementRetried();
                await Task.Delay(retryDelayMs * attempt);
                continue;
            }

            var code = (int)last.StatusCode;
            if (last.IsSuccessStatusCode)
            {
                // Validate JSON shape lightly so we don't hand RealServer garbage.
                try
                {
                    using var doc = JsonDocument.Parse(lastText);
                    if (doc.RootElement.TryGetProperty("code", out var c) && c.GetInt32() != 0)
                    {
                        Console.WriteLine($"[proxy] {cmd} upstream code={c} body={Trim(lastText, 160)}");
                    }
                }
                catch { /* pass through raw */ }

                stats.IncrementSuccess();
                if (attempt > 1)
                    Console.WriteLine($"[proxy] {cmd} ok after {attempt} attempts");
                return Results.Content(lastText!, "application/json", Encoding.UTF8, statusCode: code);
            }

            var retriable = code is 401 or 408 or 429 or >= 500;
            Console.WriteLine($"[proxy] {cmd} attempt {attempt}/{maxRetries} HTTP {code} {Trim(lastText, 120)}");
            if (!retriable || attempt == maxRetries)
            {
                stats.IncrementFailed();
                return Results.Content(lastText ?? $"{{\"code\":-1,\"message\":\"upstream HTTP {code}\"}}",
                    "application/json", Encoding.UTF8, statusCode: code);
            }

            stats.IncrementRetried();
            await Task.Delay(retryDelayMs * attempt);
        }

        stats.IncrementFailed();
        return Results.Json(new { code = -1, message = "exhausted retries" }, statusCode: 502);
    }
    finally
    {
        gate.Release();
    }
});

Console.WriteLine("============================================================");
Console.WriteLine(" LocalSignProxy");
Console.WriteLine($"   listen   : {builder.Configuration["Urls"] ?? "http://127.0.0.1:18488"}");
Console.WriteLine($"   upstream : {upstreamUrl}");
Console.WriteLine($"   token    : {(string.IsNullOrWhiteSpace(upstreamToken) ? "(none)" : upstreamToken[..Math.Min(8, upstreamToken.Length)] + "…")}");
Console.WriteLine($"   cmd log  : {cmdLogPath}");
Console.WriteLine(" App → 设置 → 使用自建签名服务器 = 开");
Console.WriteLine("      签名服务器 URL = http://127.0.0.1:18488");
Console.WriteLine("      API Key 可留空（代理会注入 UpstreamToken）或仍填同一 token");
Console.WriteLine("============================================================");

app.Run();

static string Trim(string? s, int n)
{
    if (string.IsNullOrEmpty(s)) return "";
    s = s.Replace('\n', ' ').Replace('\r', ' ');
    return s.Length <= n ? s : s[..n] + "…";
}

sealed class SignStats
{
    private long _total, _success, _failed, _retried;
    public long Total => Interlocked.Read(ref _total);
    public long Success => Interlocked.Read(ref _success);
    public long Failed => Interlocked.Read(ref _failed);
    public long Retried => Interlocked.Read(ref _retried);
    public void IncrementTotal() => Interlocked.Increment(ref _total);
    public void IncrementSuccess() => Interlocked.Increment(ref _success);
    public void IncrementFailed() => Interlocked.Increment(ref _failed);
    public void IncrementRetried() => Interlocked.Increment(ref _retried);
    public object Snapshot() => new { Total, Success, Failed, Retried };
}

/// <summary>
/// Ring-buffer + histogram of sign commands. Used for protocol research
/// (e.g. cloud pin/mute capture correlation). Does not store payload hex.
/// </summary>
sealed class CommandLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly LinkedList<(string ts, string cmd, long bodyLen)> _recent = new();
    private const int RecentCap = 200;

    public CommandLog(string path)
    {
        _path = path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* best-effort */ }
    }

    public void Record(string cmd, long bodyLen)
    {
        if (string.IsNullOrWhiteSpace(cmd)) cmd = "?";
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffK");
        lock (_gate)
        {
            _counts.TryGetValue(cmd, out var n);
            _counts[cmd] = n + 1;
            _recent.AddFirst((ts, cmd, bodyLen));
            while (_recent.Count > RecentCap) _recent.RemoveLast();
            try
            {
                var line = JsonSerializer.Serialize(new { ts, cmd, bodyLen }) + Environment.NewLine;
                File.AppendAllText(_path, line);
            }
            catch { /* disk full / path missing */ }
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                path = _path,
                totalDistinct = _counts.Count,
                histogram = _counts.OrderByDescending(kv => kv.Value)
                    .Select(kv => new { cmd = kv.Key, count = kv.Value })
                    .Take(100)
                    .ToArray(),
                recent = _recent.Select(x => new { x.ts, x.cmd, x.bodyLen }).Take(50).ToArray(),
            };
        }
    }
}
