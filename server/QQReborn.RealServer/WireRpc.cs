using System.Text.Json.Nodes;
using QQReborn.RealServer.Wire;

namespace QQReborn.RealServer;

/// <summary>
/// Shell wire protocol entry: parse frame → <see cref="WireDispatch"/> → JSON result.
/// Transport (accept / auth / send) stays in Program.cs.
/// </summary>
public static class WireRpc
{
    public static async Task<string?> HandleAsync(string text, ClientConnection conn, SessionHub hub)
    {
        JsonObject req;
        try { req = (JsonObject)JsonNode.Parse(text)!; }
        catch { return null; }

        var id = WireJson.S(req, "id") ?? "";
        var type = WireJson.S(req, "type") ?? "";
        var sessions = hub.BackendFor(conn);

        try
        {
            var (data, error, handled) = await WireDispatch.TryHandleAsync(type, req, sessions, conn);
            if (!handled)
                data = null;

            var resp = new JsonObject { ["id"] = id, ["type"] = "result", ["data"] = data };
            if (error != null) resp["error"] = error;
            return resp.ToJsonString();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] HandleAsync(" + type + "): " + ex);
            return new JsonObject
            {
                ["id"] = id,
                ["type"] = "result",
                ["data"] = null,
                ["error"] = ex.Message,
            }.ToJsonString();
        }
    }
}
