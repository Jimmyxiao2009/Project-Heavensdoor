namespace QQReborn.ServerHost;

public sealed class OneClickResult
{
    public bool Success { get; init; }
    public bool NapCatOnline { get; init; }
    public bool OpenedBrowser { get; init; }
    public string Message { get; init; } = "";
    public string AccessPassword { get; init; } = "";
    public int Port { get; init; }

    public static OneClickResult Ok(bool napCatOnline, string accessPassword, int port) => new()
    {
        Success = true,
        NapCatOnline = napCatOnline,
        AccessPassword = accessPassword,
        Port = port,
        Message = napCatOnline
            ? "环境已就绪：NapCat 在线，网关已启动。"
            : "网关已启动；请在 QQ 窗口完成登录后点「检测 NapCat」。",
    };

    public static OneClickResult Fail(string message, bool napCatOnline = false, bool openedBrowser = false) => new()
    {
        Success = false,
        NapCatOnline = napCatOnline,
        OpenedBrowser = openedBrowser,
        Message = message,
    };
}
