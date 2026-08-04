using System.Text.Json.Nodes;

namespace QQReborn.RealServer.Wire;

/// <summary>Shared JSON field readers for wire request objects.</summary>
public static class WireJson
{
    public static string? S(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double N(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    public static bool? B(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static bool Flag(JsonObject o, string k, bool defaultValue = false)
    {
        if (o[k] is not JsonValue jv) return defaultValue;
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<double>(out var d)) return d != 0;
        if (jv.TryGetValue<string>(out var s))
            return !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        return defaultValue;
    }
}
