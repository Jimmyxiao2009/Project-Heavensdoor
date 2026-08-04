using System.Security.Cryptography;
using System.Text;

namespace QQReborn.RealServer;

/// <summary>Access-password helpers for the WebSocket auth frame.</summary>
public static class WireAuth
{
    /// <summary>Length-safe constant-time password compare (FixedTimeEquals throws on length mismatch).</summary>
    public static bool SafePasswordEquals(string? provided, string? expected)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? ""));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? ""));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
