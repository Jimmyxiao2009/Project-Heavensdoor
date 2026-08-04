using System;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>QR code for real-account login, pushed by QQReborn.RealServer.</summary>
    public class QrCodeInfo
    {
        public string Url { get; set; }
        public string ImageBase64 { get; set; }
    }

    public class VoicePlayableResult
    {
        public byte[] Bytes { get; set; } = new byte[0];
        public string Format { get; set; } = "";
        public int Duration { get; set; }
    }

    /// <summary>Real-account login progress, pushed by QQReborn.RealServer.</summary>
    public class LoginStatusInfo
    {
        public string State { get; set; }
        public long Uin { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Pushed when any client changes pin/mute via setConversationFlags.</summary>
    public class ConversationFlagsChangedInfo
    {
        public string ConversationId { get; set; }
        public bool IsPinned { get; set; }
        public bool IsMuted { get; set; }
    }

    public class ConversationReadInfo
    {
        public string ConversationId { get; set; }
        public string LastReadAt { get; set; }
    }

    /// <summary>Pushed when NapCat reports a friend/group message recall.</summary>
    public class MessageRecalledInfo
    {
        public string ConversationId { get; set; }
        public string MessageId { get; set; }
        public long NapCatMessageId { get; set; }
        public long OperatorUin { get; set; }
        public long SenderUin { get; set; }
        public string SenderName { get; set; }
        public string Preview { get; set; }
    }
}
