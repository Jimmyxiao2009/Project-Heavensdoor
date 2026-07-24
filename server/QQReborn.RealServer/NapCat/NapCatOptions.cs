namespace QQReborn.RealServer.NapCat;

public sealed class NapCatOptions
{
    /// <summary>OneBot HTTP API root, e.g. http://127.0.0.1:3000</summary>
    public string HttpBase { get; set; } = "http://127.0.0.1:3000";

    /// <summary>Forward WebSocket for events, e.g. ws://127.0.0.1:3001</summary>
    public string EventWs { get; set; } = "ws://127.0.0.1:3001";

    /// <summary>Optional Bearer / access_token query for NapCat auth.</summary>
    public string AccessToken { get; set; } = "";

    public int ReconnectDelayMs { get; set; } = 2000;
}
