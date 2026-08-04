using Microsoft.Extensions.Configuration;
using QQReborn.RealServer.NapCat;

namespace QQReborn.RealServer;

/// <summary>
/// Creates the session backend. Product path is always NapCat local OneBot gateway.
/// </summary>
public static class BackendFactory
{
    public const string NapCat = "napcat";

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
        Console.WriteLine($"[Backend] selected = {NapCat}");

        var opts = new NapCatOptions();
        config.GetSection("NapCat").Bind(opts);

        // Env overrides (steward / scripts)
        var http = Environment.GetEnvironmentVariable("NAPCAT_HTTP");
        var ws = Environment.GetEnvironmentVariable("NAPCAT_WS");
        var token = Environment.GetEnvironmentVariable("NAPCAT_TOKEN");
        if (!string.IsNullOrWhiteSpace(http)) opts.HttpBase = http.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(ws)) opts.EventWs = ws.Trim();
        if (!string.IsNullOrWhiteSpace(token)) opts.AccessToken = token.Trim();

        return new NapCatSessionManager(opts);
    }

    private static string Normalize(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "napcat" or "onebot" or "ob11" or "ntqq" or "local" or "localgateway" => NapCat,
        _ => NapCat,
    };
}
