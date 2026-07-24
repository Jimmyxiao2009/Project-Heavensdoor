using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

/// <summary>Minimal OneBot 11 HTTP client compatible with NapCat.</summary>
public sealed class NapCatApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly NapCatOptions _opts;

    public NapCatApiClient(NapCatOptions opts)
    {
        _opts = opts;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(opts.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.AccessToken);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Call OneBot action. Tries POST {base}/{action} with params body, then
    /// POST {base} with {action,params} envelope.
    /// Returns (data node, error message).
    /// </summary>
    public async Task<(JsonNode? data, string? error)> CallAsync(string action, JsonObject? parameters = null, CancellationToken ct = default)
    {
        parameters ??= new JsonObject();
        var baseUrl = _opts.HttpBase.TrimEnd('/');

        // Style A: NapCat / go-cqhttp path style
        try
        {
            var url = $"{baseUrl}/{action.TrimStart('/')}";
            if (!string.IsNullOrWhiteSpace(_opts.AccessToken) && !url.Contains("access_token", StringComparison.OrdinalIgnoreCase))
                url += (url.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(_opts.AccessToken);

            using var content = new StringContent(parameters.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return ParseOneBotResponse(text, (int)resp.StatusCode);
        }
        catch (Exception exA)
        {
            Console.WriteLine($"[NapCat] path-style {action} failed: {exA.Message}; try envelope");
        }

        // Style B: unified envelope
        try
        {
            var envelope = new JsonObject
            {
                ["action"] = action,
                ["params"] = parameters.DeepClone(),
                ["echo"] = Guid.NewGuid().ToString("N"),
            };
            var url = baseUrl;
            if (!string.IsNullOrWhiteSpace(_opts.AccessToken))
                url += (url.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(_opts.AccessToken);

            using var content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return ParseOneBotResponse(text, (int)resp.StatusCode);
        }
        catch (Exception exB)
        {
            return (null, "napcat-http: " + exB.Message);
        }
    }

    private static (JsonNode? data, string? error) ParseOneBotResponse(string text, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, $"empty-response HTTP {statusCode}");

        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (Exception ex) { return (null, "invalid-json: " + ex.Message); }

        // { status, retcode, data, message/wording }
        var status = root?["status"]?.GetValue<string>();
        var retcode = root?["retcode"] is JsonValue rv && rv.TryGetValue<int>(out var rc) ? rc
            : root?["retcode"] is JsonValue rd && rd.TryGetValue<double>(out var rcd) ? (int)rcd
            : statusCode >= 400 ? statusCode : 0;

        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && retcode != 0)
        {
            var msg = root?["message"]?.GetValue<string>()
                      ?? root?["wording"]?.GetValue<string>()
                      ?? root?["msg"]?.GetValue<string>()
                      ?? $"retcode={retcode}";
            return (root?["data"], "napcat: " + msg);
        }

        return (root?["data"], null);
    }

    public static long ReadLong(JsonNode? n)
    {
        if (n is null) return 0;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return 0;
    }

    public static string ReadStr(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s ?? "",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(),
        JsonValue v when v.TryGetValue<double>(out var d) => ((long)d).ToString(),
        _ => n.ToJsonString().Trim('"'),
    };
}
