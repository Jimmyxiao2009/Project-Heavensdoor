using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Converts a protocol message into the short, user-visible summary shared by conversation
    /// rows, reply previews, in-app banners, and system notifications.
    /// </summary>
    public static class MessagePresentation
    {
        public static string GetSummary(ChatMessage message)
        {
            if (message == null) return string.Empty;
            if (message.IsImage) return "[图片]";
            if (message.IsSticker) return "[表情]";
            if (message.IsVoice) return "[语音]";
            if (message.IsVideo) return "[视频]";
            if (message.IsLinkCard) return "[链接]";
            if (message.IsFile) return "[文件]";
            if (message.IsLocation) return "[位置]";
            return message.Text ?? string.Empty;
        }
    }
}
