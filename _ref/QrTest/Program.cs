using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using Net.Codecrete.QrCodeGenerator;
using QQReborn.Signing;

// =====================================================================================
// QQ-Reborn P3: QR-login chain check against LagrangeV2 (the maintained successor).
//
// Why this rewrite: v1 (_ref/Lagrange.Core) is ARCHIVED and its public sign URL
// (sign.lagrangecore.org/api/sign/{version}, no auth) returns 404 -> FetchQrCode returned
// null forever. LagrangeV2 + the token-authenticated /api/sign/sec-sign endpoint is the
// live path. The token comes from the #signer GPG registration (see project memory).
//
// This harness will run end-to-end the moment a valid token is in signer.config.json.
// Without a token it still runs, but the sign call returns null and Login() returns false
// (reported clearly, then the bot is disposed -- no zombie reconnect loop like v1 left).
// =====================================================================================

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
Console.WriteLine("=== QQ-Reborn P3: LagrangeV2 QR-login chain check ===");

var cfg = LoadConfig();
Console.WriteLine($"Sign server : {cfg.Url}");
Console.WriteLine(string.IsNullOrEmpty(cfg.Token)
    ? "Sign token  : (none) -- login WILL fail at the sign step until you register a token.\n"
      + "              Put it in signer.config.json next to this exe. See signer.config.example.json."
    : $"Sign token  : present ({cfg.Token!.Length} chars)");

var sign = new TokenSignProvider(cfg.Url, cfg.Token, cfg.Uin);

var config = new BotConfig
{
    Protocol = Protocols.Linux,
    SignProvider = sign,
    AutoReconnect = true,
    AutoReLogin = true,
    GetOptimumServer = true,
    UseIPv6Network = false,
    LogLevel = LogLevel.Debug,
};

// Reuse a previous session if we have one (skips the QR scan).
const string keystorePath = "keystore.json";
BotKeystore? keystore = null;
if (File.Exists(keystorePath))
{
    try
    {
        keystore = JsonSerializer.Deserialize<BotKeystore>(await File.ReadAllTextAsync(keystorePath));
        Console.WriteLine("Loaded existing keystore.json -- attempting session resume.");
    }
    catch (Exception e)
    {
        Console.WriteLine($"keystore.json present but unreadable ({e.Message}); starting fresh.");
    }
}

var bot = keystore is null
    ? BotFactory.Create(config)
    : BotFactory.Create(config, keystore);

bot.EventInvoker.RegisterEvent<BotLogEvent>((_, e) =>
    Console.WriteLine($"[LOG/{e.Level}] [{e.Tag}] {e.Message}"));

bot.EventInvoker.RegisterEvent<BotQrCodeEvent>((_, e) =>
{
    Console.WriteLine($"\n[QR] {e.Url}");
    var png = Path.Combine(AppContext.BaseDirectory, "qrcode.png");
    try { File.WriteAllBytes(png, e.Image); Console.WriteLine($"[QR] saved PNG -> {png}"); }
    catch (Exception ex) { Console.WriteLine($"[QR] PNG save failed: {ex.Message}"); }
    PrintQr(e.Url);
    Console.WriteLine("[QR] Scan with an already-logged-in QQ on another device, then confirm.\n");
});

bot.EventInvoker.RegisterEvent<BotRefreshKeystoreEvent>(async (_, e) =>
{
    try
    {
        await File.WriteAllTextAsync(keystorePath, JsonSerializer.Serialize(e.Keystore));
        Console.WriteLine("[KEYSTORE] session saved -> keystore.json");
    }
    catch (Exception ex) { Console.WriteLine($"[KEYSTORE] save failed: {ex.Message}"); }
});

bot.EventInvoker.RegisterEvent<BotOnlineEvent>((_, e) =>
    Console.WriteLine($"\n*** ONLINE *** ({e.Reason})  uin={bot.BotUin}\n"));

bot.EventInvoker.RegisterEvent<BotOfflineEvent>((_, e) =>
    Console.WriteLine($"[OFFLINE] {e.ToEventMessage()}"));

bot.EventInvoker.RegisterEvent<BotCaptchaEvent>((_, e) =>
    Console.WriteLine($"[CAPTCHA] needed: {e.ToEventMessage()} -- submit via SubmitCaptcha()."));

bot.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>((_, e) =>
    Console.WriteLine($"[NEW DEVICE VERIFY] {e.ToEventMessage()}"));

bot.EventInvoker.RegisterEvent<BotSMSEvent>((_, e) =>
    Console.WriteLine($"[SMS] {e.ToEventMessage()}"));

bot.EventInvoker.RegisterEvent<BotMessageEvent>((_, e) =>
{
    var preview = string.Join(" ", e.Message.Entities
        .Select(ent => ent is Lagrange.Core.Message.Entities.TextEntity t ? t.Text : $"[{ent.GetType().Name}]"));
    Console.WriteLine($"[MSG] from {e.Message.Contact.Nickname}({e.Message.Contact.Uin}): {preview}");
});

Console.WriteLine("\nCalling Login() (fetches QR, then polls until you scan + confirm)...");
bool ok;
try
{
    ok = await bot.Login();
}
catch (Exception e)
{
    Console.WriteLine($"!! Login threw: {e}");
    ok = false;
}

if (!ok)
{
    Console.WriteLine(
        "\n!! Login FAILED.\n" +
        (string.IsNullOrEmpty(cfg.Token)
            ? "   Root cause: no sign token. Register one (#signer register/verify/bind via GPG),\n" +
              "   drop it into signer.config.json, and re-run.\n"
            : "   The sign token is set but the chain still failed -- check the [LOG/Warning] sign lines\n" +
              "   above (HTTP code / server code). Token may be unbound for this uin, expired, or rate-limited.\n"));
    bot.Dispose();   // v1 left a zombie reconnect/heartbeat loop here; V2 path tears down cleanly.
    return;
}

Console.WriteLine("Login OK. Staying online -- send yourself a QQ message to test receive. Ctrl+C to quit.");

// ==== Channel SSO SendPacket test v2 ====
Console.WriteLine("\n--- Channel SSO SendPacket test v2 ---");
try
{
    // Helper: encode a simple protobuf with a few varint fields
    static byte[] Pb(params (int field, ulong value)[] fields)
    {
        using var ms = new MemoryStream();
        foreach (var (fn, val) in fields)
        {
            // tag
            ulong tag = (ulong)(fn << 3 | 0);
            while (tag > 0x7F) { ms.WriteByte((byte)((tag & 0x7F) | 0x80)); tag >>= 7; }
            ms.WriteByte((byte)(tag & 0x7F));
            // varint value
            ulong v = val;
            while (v > 0x7F) { ms.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            ms.WriteByte((byte)(v & 0x7F));
        }
        return ms.ToArray();
    }

    // Decode helper
    static void DumpProto(byte[] data, string label)
    {
        Console.WriteLine($"  {label} ({data.Length}B):");
        int pos = 0;
        while (pos < data.Length && pos < 500)
        {
            ulong tag = 0; int shift = 0;
            while (pos < data.Length) { byte b = data[pos++]; tag |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; }
            int fn = (int)(tag >> 3), wt = (int)(tag & 7);
            if (wt == 0) { ulong v = 0; shift = 0; while (pos < data.Length) { byte b = data[pos++]; v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; } Console.WriteLine($"    f{fn}: {v}"); }
            else if (wt == 2) { ulong len = 0; shift = 0; while (pos < data.Length) { byte b = data[pos++]; len |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; } var chunk = data[pos..(int)(pos+(int)len)]; pos += (int)len; try { var s = System.Text.Encoding.UTF8.GetString(chunk); if (s.All(c => !char.IsControl(c) || c=='\n')) Console.WriteLine($"    f{fn}: \"{s[..Math.Min(60,s.Length)]}\""); else Console.WriteLine($"    f{fn}: bytes[{len}]"); } catch { Console.WriteLine($"    f{fn}: bytes[{len}]"); } }
            else break;
        }
    }

    async Task Probe(string cmd, byte[] body, string desc)
    {
        try
        {
            var packet = new Lagrange.Core.Common.Entity.BotSsoPacket(cmd, body);
            var resp = await bot.SendPacket(packet);
            Console.WriteLine($"\n[{cmd}] {desc} → retCode={resp.RetCode}, seq={resp.Sequence}");
            if (resp.Data.Length > 0) DumpProto(resp.Data.ToArray(), "Response");
            else Console.WriteLine("  (empty response)");
        }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); }
    }

    // 1. SyncFirstView — decode the known-good response
    Console.WriteLine("\n=== 1. SyncFirstView (known working) ===");
    await Probe("trpc.group_pro.synclogic.SyncLogic.SyncFirstView", [], "empty body");

    // Only test slash-format commands (pd.qq.com style) and working group_pro commands
    Console.WriteLine("\n=== Slash-format & group_pro probing ===");
    ulong knownGuild = 144115218702922042;

    // These commands are in the whitelist but haven't been tested
    var probes = new (string cmd, byte[] body, string desc)[]
    {
        // group_pro with slashes (pd.qq.com HTTP format)
        ("trpc.group_pro.notice_list.Handler/GetList", Pb((1,knownGuild)), "notice_list.GetList"),
        ("trpc.group_pro.cmd0xf5b.GetAggregationalMemberList/HandleProcess", Pb((1,knownGuild)), "GetAggregationalMemberList"),
        ("trpc.group_pro.cmd0xf89.GetGroupProUserHeadPortrait/HandleProcess", Pb((1,knownGuild)), "GetGroupProUserHeadPortrait"),
        ("trpc.group_pro.member_search_interface.Handler/HandleProcess6", Pb((1,knownGuild)), "member_search"),
        // Try QChannel commands WITHOUT prefix, WITH slash for method
        ("trpc.qchannel.commreader.ComReader/GetGuildList", [], "qchannel/GetGuildList empty"),
        ("trpc.qchannel.commreader.ComReader/GetGuildList", Pb((1,0),(2,20)), "qchannel/GetGuildList f1=0,f2=20"),
        ("trpc.qchannel.commreader.ComReader/GetGuildFeeds", Pb((1,knownGuild)), "qchannel/GetGuildFeeds"),
        ("trpc.qchannel.commreader.ComReader/GetNotices1", Pb((1,knownGuild)), "qchannel/GetNotices1"),
    };

    foreach (var (cmd, body, desc) in probes)
    {
        await Probe(cmd, body, desc);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Channel SSO test failed: {ex}");
}
Console.WriteLine("--- end channel SSO test ---\n");

Console.WriteLine("Tests done. Exiting.");
AppDomain.CurrentDomain.ProcessExit += (_, _) => bot.Dispose();
bot.Dispose();
return;


// --- helpers -------------------------------------------------------------------------

static SignerConfig LoadConfig()
{
    foreach (var path in new[]
             {
                 Path.Combine(AppContext.BaseDirectory, "signer.config.json"),
                 "signer.config.json",
             })
    {
        if (!File.Exists(path)) continue;
        try
        {
            var c = JsonSerializer.Deserialize<SignerConfig>(File.ReadAllText(path));
            if (c is not null) return c with { Url = string.IsNullOrWhiteSpace(c.Url) ? SignerConfig.DefaultUrl : c.Url };
        }
        catch (Exception e) { Console.WriteLine($"[CONFIG] {path} unreadable: {e.Message}"); }
    }
    Console.WriteLine("[CONFIG] no signer.config.json found -- using defaults (no token).");
    return new SignerConfig();
}

static void PrintQr(string text)
{
    try
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Low);
        for (var y = 0; y < qr.Size + 2; y += 2)
        {
            for (var x = 0; x < qr.Size + 2; x++)
            {
                var top = qr.GetModule(x - 1, y - 1);
                var bottom = qr.GetModule(x - 1, y) || y > qr.Size;
                Console.Write(!top && !bottom ? "█" : top && !bottom ? "▄" : !top ? "▀" : " ");
            }
            Console.Write('\n');
        }
    }
    catch (Exception e) { Console.WriteLine($"[QR] ascii render failed: {e.Message}"); }
}

internal sealed record SignerConfig
{
    public const string DefaultUrl = "https://sign.lagrangecore.org";

    [JsonPropertyName("url")] public string Url { get; init; } = DefaultUrl;
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("uin")] public long Uin { get; init; }
}
