namespace QQReborn.Shell.Logic.Tests;

/// <summary>
/// Mirrors <c>MessagePresentation.GetSummary</c> pure rules so we can guard
/// list/banner preview text without pulling the UWP project into the test host.
/// Keep in sync with MessagePresentation.cs.
/// </summary>
public class MessageSummaryLogicTests
{
    private static string Summary(string contentType, string text)
    {
        switch (contentType)
        {
            case "Image": return "[图片]";
            case "Sticker": return "[表情]";
            case "Voice": return "[语音]";
            case "Video": return "[视频]";
            case "LinkCard": return "[链接]";
            case "FileMsg": return "[文件]";
            case "Location": return "[位置]";
            default: return text ?? string.Empty;
        }
    }

    [Theory]
    [InlineData("Image", "x", "[图片]")]
    [InlineData("Voice", "x", "[语音]")]
    [InlineData("Text", "hello", "hello")]
    [InlineData("Text", "", "")]
    public void Preview_rules(string type, string text, string expected)
    {
        Assert.Equal(expected, Summary(type, text));
    }
}
