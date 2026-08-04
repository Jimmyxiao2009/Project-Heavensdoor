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
            return GetSummary(message.ContentType, message.Text);
        }

        /// <summary>Pure overload for unit tests / non-UI callers.</summary>
        public static string GetSummary(MessageContentType contentType, string text)
        {
            switch (contentType)
            {
                case MessageContentType.Image: return "[图片]";
                case MessageContentType.Sticker: return "[表情]";
                case MessageContentType.Voice: return "[语音]";
                case MessageContentType.Video: return "[视频]";
                case MessageContentType.LinkCard: return "[链接]";
                case MessageContentType.FileMsg: return "[文件]";
                case MessageContentType.Location: return "[位置]";
                default: return text ?? string.Empty;
            }
        }
    }
}
