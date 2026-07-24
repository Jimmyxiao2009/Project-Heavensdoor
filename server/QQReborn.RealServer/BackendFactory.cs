using Microsoft.Extensions.Configuration;
using QQReborn.RealServer.NapCat;

namespace QQReborn.RealServer;

public static class BackendFactory
{
    public const string NapCat = "napcat";

    /// <summary>
    /// Resolve backend id. Production path is always NapCat local gateway.
    /// Env QQREBORN_BACKEND / config QQReborn:Backend may still say "napcat";
    /// any other value is treated as napcat (Lagrange removed).
    /// </summary>
    public static string ResolveBackendId(IConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("QQREBORN_BACKEND");
        if (!string.IsNullOrWhiteSpace(env)) return Normalize(env);

        var cfg = config["QQReborn:Backend"] ?? config["Backend"];
        if (!string.IsNullOrWhiteSpace(cfg)) return Normalize(cfg);

        return NapCat;
    }

    public static ISessionBackend Create(IConfiguration config)
    {
        var id = ResolveBackendId(config);
        if (id != NapCat)
            Console.WriteLine($"[Backend] '{id}' is no longer supported; using napcat");
        Console.WriteLine($"[Backend] selected = {NapCat}");

        var opts = new NapCatOptions();
        config.GetSection("NapCat").Bind(opts);
        // Env overrides for headless deploy
        var http = Environment.GetEnvironmentVariable("NAPCAT_HTTP");
        var ws = Environment.GetEnvironmentVariable("NAPCAT_WS");
        var token = Environment.GetEnvironmentVariable("NAPCAT_TOKEN");
        if (!string.IsNullOrWhiteSpace(http)) opts.HttpBase = http.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(ws)) opts.EventWs = ws.Trim();
        if (!string.IsNullOrWhiteSpace(token)) opts.AccessToken = token.Trim();
        return new NapCatSessionManager(opts);
    }

    private static string Normalize(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s switch
        {
            "napcat" or "onebot" or "ob11" or "ntqq" or "local" or "localgateway" => NapCat,
            // Former Lagrange aliases map to napcat (product path removed).
            "lagrange" or "lagrangev2" or "lv2" or "real" => NapCat,
            _ => NapCat,
        };
    }
}
