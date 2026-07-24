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
    /// <summary>Small best-effort snapshot used to render MainPage immediately.</summary>
    public static class ConversationCache
    {
        private const string FileName = "home_conversations.json";

        public static async Task<List<ChatConversation>> LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                var text = await FileIO.ReadTextAsync(file);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ConversationDto>));
                    var dtos = serializer.ReadObject(stream) as List<ConversationDto>;
                    var result = new List<ChatConversation>();
                    if (dtos == null) return result;
                    foreach (var dto in dtos) result.Add(dto.ToModel());
                    return result;
                }
            }
            catch
            {
                return new List<ChatConversation>();
            }
        }

        public static async Task SaveAsync(IEnumerable<ChatConversation> conversations)
        {
            try
            {
                var dtos = new List<ConversationDto>();
                foreach (var c in conversations)
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    dtos.Add(ConversationDto.FromModel(c));
                }

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ConversationDto>));
                    serializer.WriteObject(stream, dtos);
                    await FileIO.WriteTextAsync(file, Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
            catch
            {
                // Cache is an optimization; never affect the live UI.
            }
        }

        private sealed class ConversationDto
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Title { get; set; }
            public string AvatarPath { get; set; }
            public string Announcement { get; set; }
            public string Preview { get; set; }
            public string LastTime { get; set; }
            public int Unread { get; set; }
            public bool IsPinned { get; set; }
            public bool IsMuted { get; set; }

            public static ConversationDto FromModel(ChatConversation c) => new ConversationDto
            {
                Id = c.Id,
                Kind = c.Kind.ToString(),
                Title = c.Title,
                AvatarPath = c.AvatarPath,
                Announcement = c.Announcement,
                Preview = c.Preview,
                LastTime = c.LastTime.ToString("o"),
                Unread = c.Unread,
                IsPinned = c.IsPinned,
                IsMuted = c.IsMuted,
            };

            public ChatConversation ToModel()
            {
                ConversationKind kind;
                if (!Enum.TryParse(Kind, true, out kind)) kind = ConversationKind.Friend;
                DateTimeOffset time;
                if (!DateTimeOffset.TryParse(LastTime, out time)) time = DateTimeOffset.Now;
                return new ChatConversation
                {
                    Id = Id,
                    Kind = kind,
                    Title = Title,
                    AvatarPath = AvatarPath,
                    Announcement = Announcement,
                    Preview = Preview,
                    LastTime = time,
                    Unread = Unread,
                    IsPinned = IsPinned,
                    IsMuted = IsMuted,
                };
            }
        }
    }
}
