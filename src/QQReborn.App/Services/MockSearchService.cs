using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Self-contained global-search backend. It pulls live data from the app's
    /// <see cref="IChatService"/> singleton (conversations / contacts / messages,
    /// read-only) and blends in an extra fake directory so the 联系人 / 群聊 sections
    /// stay rich even before the user has chatted with everyone.
    /// </summary>
    public class MockSearchService : ISearchService
    {
        private const string FriendAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
        private const string GroupAvatar = "ms-appx:///Assets/Avatars/DefaultTroopAvatar.png";

        private readonly IChatService _chat;

        // Extra fake friends not necessarily present as active conversations.
        private readonly List<Contact> _extraContacts = new List<Contact>
        {
            new Contact { Uin = 30001, Name = "赵敏", AvatarPath = FriendAvatar, Signature = "明教光明左使", Online = true },
            new Contact { Uin = 30002, Name = "周芷若", AvatarPath = FriendAvatar, Signature = "峨眉掌门", Online = false },
            new Contact { Uin = 30003, Name = "陈经理", AvatarPath = FriendAvatar, Signature = "本周五前交方案", Online = true },
            new Contact { Uin = 30004, Name = "快递小哥", AvatarPath = FriendAvatar, Signature = "您的包裹已到驿站", Online = false },
            new Contact { Uin = 30005, Name = "老同学小明", AvatarPath = FriendAvatar, Signature = "好久不见", Online = true },
            new Contact { Uin = 30006, Name = "健身教练", AvatarPath = FriendAvatar, Signature = "今天练腿了吗", Online = false },
            new Contact { Uin = 30007, Name = "房东大爷", AvatarPath = FriendAvatar, Signature = "房租该交了", Online = false },
            new Contact { Uin = 30008, Name = "产品经理 Tom", AvatarPath = FriendAvatar, Signature = "这个需求很简单", Online = true },
        };

        // Extra fake groups (searchable even if no active conversation row exists).
        private readonly List<ChatConversation> _extraGroups = new List<ChatConversation>
        {
            new ChatConversation { Id = "g-100", Kind = ConversationKind.Group, Title = "大学室友群", AvatarPath = GroupAvatar, Preview = "周末开黑吗", LastTime = DateTimeOffset.Now.AddHours(-5) },
            new ChatConversation { Id = "g-101", Kind = ConversationKind.Group, Title = "Lumia 老用户俱乐部", AvatarPath = GroupAvatar, Preview = "950 XL 永不为奴", LastTime = DateTimeOffset.Now.AddDays(-2) },
            new ChatConversation { Id = "g-102", Kind = ConversationKind.Group, Title = "公司技术部", AvatarPath = GroupAvatar, Preview = "发版了记得回归", LastTime = DateTimeOffset.Now.AddHours(-9) },
            new ChatConversation { Id = "g-103", Kind = ConversationKind.Group, Title = "羽毛球周末局", AvatarPath = GroupAvatar, Preview = "本周六体育馆 3 号场", LastTime = DateTimeOffset.Now.AddDays(-1) },
        };

        public MockSearchService(IChatService chat)
        {
            _chat = chat;
        }

        public async Task<IReadOnlyList<SearchSection>> SearchAsync(string query)
        {
            var sections = new List<SearchSection>();
            if (string.IsNullOrWhiteSpace(query)) return sections;

            var q = query.Trim();
            var conversations = await _chat.GetConversationsAsync();
            var contacts = await _chat.GetContactsAsync();

            // ---- 联系人 (contacts) ----
            // Merge the live contacts with the extra fake directory, de-duped by Uin.
            var allContacts = new Dictionary<long, Contact>();
            foreach (var c in contacts) allContacts[c.Uin] = c;
            foreach (var c in _extraContacts) if (!allContacts.ContainsKey(c.Uin)) allContacts[c.Uin] = c;

            var contactHits = new List<SearchResult>();
            foreach (var c in allContacts.Values
                         .Where(c => Contains(c.Name, q) || Contains(c.Signature, q))
                         .OrderByDescending(c => Contains(c.Name, q))
                         .ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                contactHits.Add(new SearchResult
                {
                    Kind = SearchResultKind.Contact,
                    Title = c.Name,
                    Subtitle = string.IsNullOrEmpty(c.Signature) ? (c.Online ? "在线" : "离线") : c.Signature,
                    AvatarPath = string.IsNullOrEmpty(c.AvatarPath) ? FriendAvatar : c.AvatarPath,
                    HasTime = false,
                    Target = new ChatConversation
                    {
                        Id = "contact-" + c.Uin,
                        Kind = ConversationKind.Friend,
                        Title = c.Name,
                        AvatarPath = string.IsNullOrEmpty(c.AvatarPath) ? FriendAvatar : c.AvatarPath
                    }
                });
            }
            if (contactHits.Count > 0)
                sections.Add(new SearchSection { Header = "联系人", Items = contactHits });

            // ---- 群聊 (group conversations) ----
            var allGroups = new Dictionary<string, ChatConversation>();
            foreach (var c in conversations.Where(c => c.Kind == ConversationKind.Group)) allGroups[c.Id] = c;
            foreach (var c in _extraGroups) if (!allGroups.ContainsKey(c.Id)) allGroups[c.Id] = c;

            var groupHits = new List<SearchResult>();
            foreach (var g in allGroups.Values
                         .Where(g => Contains(g.Title, q) || Contains(g.Preview, q))
                         .OrderByDescending(g => g.LastTime))
            {
                groupHits.Add(new SearchResult
                {
                    Kind = SearchResultKind.Group,
                    Title = g.Title,
                    Subtitle = string.IsNullOrEmpty(g.Preview) ? "群聊" : g.Preview,
                    AvatarPath = string.IsNullOrEmpty(g.AvatarPath) ? GroupAvatar : g.AvatarPath,
                    HasTime = false,
                    Target = g
                });
            }
            if (groupHits.Count > 0)
                sections.Add(new SearchSection { Header = "群聊", Items = groupHits });

            // ---- 聊天记录 (message text) ----
            // Iterate every active conversation and scan its messages for the query.
            var messageHits = new List<SearchResult>();
            foreach (var conv in conversations)
            {
                var msgs = await _chat.GetMessagesAsync(conv.Id);
                foreach (var m in msgs)
                {
                    if (m == null || string.IsNullOrEmpty(m.Text)) continue;
                    if (!Contains(m.Text, q)) continue;

                    messageHits.Add(new SearchResult
                    {
                        Kind = SearchResultKind.Message,
                        Title = conv.Title,
                        Subtitle = BuildSnippet(m.SenderName, m.Text),
                        AvatarPath = string.IsNullOrEmpty(conv.AvatarPath)
                            ? (conv.Kind == ConversationKind.Group ? GroupAvatar : FriendAvatar)
                            : conv.AvatarPath,
                        TimeText = ChatMessage.FullTimeText(m.Time),
                        HasTime = true,
                        Target = conv
                    });
                }
            }
            if (messageHits.Count > 0)
                sections.Add(new SearchSection { Header = "聊天记录", Items = messageHits });

            return sections;
        }

        private static string BuildSnippet(string sender, string text)
        {
            var body = text ?? string.Empty;
            return string.IsNullOrEmpty(sender) ? body : sender + "：" + body;
        }

        private static bool Contains(string source, string query)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
