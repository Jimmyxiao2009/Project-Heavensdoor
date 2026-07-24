using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>Best-effort local transcript cache (no Newtonsoft — UWP project has no that package).
    /// Serialization is limited to simple ChatMessage fields; failures return empty / no-op.</summary>
    public static class MessageCache
    {
        public static async Task<List<ChatMessage>> LoadMessagesAsync(string conversationId)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var fileName = "messages_" + Sanitize(conversationId) + ".json";
                var file = await folder.GetFileAsync(fileName);
                var text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(text)) return new List<ChatMessage>();
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    var ser = new DataContractJsonSerializer(typeof(List<ChatMessageDto>));
                    var dtos = ser.ReadObject(ms) as List<ChatMessageDto>;
                    var list = new List<ChatMessage>();
                    if (dtos == null) return list;
                    foreach (var d in dtos)
                        list.Add(d.ToMessage());
                    return list;
                }
            }
            catch (Exception)
            {
                return new List<ChatMessage>();
            }
        }

        public static async Task SaveMessagesAsync(string conversationId, IEnumerable<ChatMessage> messages)
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var fileName = "messages_" + Sanitize(conversationId) + ".json";
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var dtos = new List<ChatMessageDto>();
                foreach (var m in messages)
                {
                    if (m == null || m.IsSystem) continue;
                    dtos.Add(ChatMessageDto.From(m));
                }
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(List<ChatMessageDto>));
                    ser.WriteObject(ms, dtos);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    await FileIO.WriteTextAsync(file, text);
                }
            }
            catch (Exception)
            {
                // Ignore save errors
            }
        }

        private static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unknown";
            var sb = new StringBuilder(id.Length);
            foreach (var ch in id)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        // Lightweight DTO — avoids serializing ObservableObject / collections that DataContract can't handle.
        public class ChatMessageDto
        {
            public string Id { get; set; }
            public string ConversationId { get; set; }
            public string ConversationTitle { get; set; }
            public string ConversationAvatarPath { get; set; }
            public long SenderUin { get; set; }
            public string SenderName { get; set; }
            public string SenderAvatarPath { get; set; }
            public string Direction { get; set; }
            public string ContentType { get; set; }
            public string Text { get; set; }
            public string ImagePath { get; set; }
            public string AudioPath { get; set; }
            public int VoiceSeconds { get; set; }
            public string FileName { get; set; }
            public string FileSize { get; set; }
            public string FileId { get; set; }
            public string ReplyToSender { get; set; }
            public string ReplyToText { get; set; }
            public string Time { get; set; }

            public static ChatMessageDto From(ChatMessage m)
            {
                return new ChatMessageDto
                {
                    Id = m.Id,
                    ConversationId = m.ConversationId,
                    ConversationTitle = m.ConversationTitle,
                    ConversationAvatarPath = m.ConversationAvatarPath,
                    SenderUin = m.SenderUin,
                    SenderName = m.SenderName,
                    SenderAvatarPath = m.SenderAvatarPath,
                    Direction = m.Direction == MessageDirection.Outgoing ? "Outgoing" : "Incoming",
                    ContentType = m.ContentType.ToString(),
                    Text = m.Text,
                    ImagePath = m.ImagePath,
                    AudioPath = m.AudioPath,
                    VoiceSeconds = m.VoiceSeconds,
                    FileName = m.FileName,
                    FileSize = m.FileSize,
                    FileId = m.FileId,
                    ReplyToSender = m.ReplyToSender,
                    ReplyToText = m.ReplyToText,
                    Time = m.Time.ToString("o"),
                };
            }

            public ChatMessage ToMessage()
            {
                MessageContentType ct;
                if (!Enum.TryParse(ContentType, true, out ct)) ct = MessageContentType.Text;
                DateTimeOffset t;
                if (!DateTimeOffset.TryParse(Time, out t)) t = DateTimeOffset.Now;
                return new ChatMessage
                {
                    Id = Id,
                    ConversationId = ConversationId,
                    ConversationTitle = ConversationTitle,
                    ConversationAvatarPath = ConversationAvatarPath,
                    SenderUin = SenderUin,
                    SenderName = SenderName,
                    SenderAvatarPath = SenderAvatarPath,
                    Direction = Direction == "Outgoing" ? MessageDirection.Outgoing : MessageDirection.Incoming,
                    ContentType = ct,
                    Text = Text,
                    ImagePath = ImagePath,
                    AudioPath = AudioPath,
                    VoiceSeconds = VoiceSeconds,
                    FileName = FileName,
                    FileSize = FileSize,
                    FileId = FileId,
                    ReplyToSender = ReplyToSender,
                    ReplyToText = ReplyToText,
                    Time = t,
                    State = MessageState.Sent,
                };
            }
        }
    }
}
