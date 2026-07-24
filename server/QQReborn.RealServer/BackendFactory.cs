using Microsoft.Extensions.Configuration;
using QQReborn.RealServer.NapCat;

namespace QQReborn.RealServer;

public static class BackendFactory
{
    public const string Lagrange = "lagrange";
    public const string NapCat = "napcat";

    /// <summary>
    /// Resolve backend from (priority high→low):
    /// env QQREBORN_BACKEND, config QQReborn:Backend, default lagrange.
    /// </summary>
    public static string ResolveBackendId(IConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("QQREBORN_BACKEND");
        if (!string.IsNullOrWhiteSpace(env)) return Normalize(env);

        var cfg = config["QQReborn:Backend"] ?? config["Backend"];
        if (!string.IsNullOrWhiteSpace(cfg)) return Normalize(cfg);

        return Lagrange;
    }

    public static ISessionBackend Create(IConfiguration config)
    {
        var id = ResolveBackendId(config);
        Console.WriteLine($"[Backend] selected = {id}");

        if (id == NapCat)
        {
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

        return new BotSessionManager();
    }

    private static string Normalize(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s switch
        {
            "napcat" or "onebot" or "ob11" or "ntqq" => NapCat,
            "lagrange" or "lagrangev2" or "lv2" or "real" => Lagrange,
            _ => s,
        };
    }
}
