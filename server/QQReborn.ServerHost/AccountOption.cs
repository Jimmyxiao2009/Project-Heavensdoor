namespace QQReborn.ServerHost;

/// <summary>One QQ account entry shown in the steward account picker.</summary>
public sealed class AccountOption
{
    public string Uin { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>Display in ComboBox.</summary>
    public string Display =>
        string.IsNullOrWhiteSpace(Uin)
            ? (string.IsNullOrWhiteSpace(Label) ? "扫码登录（不指定号）" : Label)
            : (string.IsNullOrWhiteSpace(Label) ? Uin : $"{Uin}  ·  {Label}");

    public override string ToString() => Display;

    public static AccountOption Main { get; } = new() { Uin = "1913695019", Label = "大号" };
    public static AccountOption Alt { get; } = new() { Uin = "2901884390", Label = "小号" };
    public static AccountOption QrScan { get; } = new() { Uin = "", Label = "扫码登录（不指定号）" };

    public static IReadOnlyList<AccountOption> Defaults { get; } = new[]
    {
        Main,
        Alt,
        QrScan,
    };
}
