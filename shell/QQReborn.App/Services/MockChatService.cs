using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// In-memory fake backend: canned conversations + messages, echoes sends, and
    /// auto-replies after a short delay so the UI behaves like a live chat during dev.
    /// </summary>
    public class MockChatService : IChatService
    {
        private const string SelfAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
        private const string FriendAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
        private const string GroupAvatar = "ms-appx:///Assets/Avatars/DefaultTroopAvatar.png";

        private readonly SelfProfile _self = new SelfProfile
        {
            Uin = 10001,
            Nickname = "Jimmy",
            AvatarPath = SelfAvatar,
            Signature = "WP 永不为奴"
        };

        private readonly List<Contact> _contacts = new List<Contact>
        {
            new Contact { Uin = 20001, Name = "张三", AvatarPath = FriendAvatar, Signature = "今天也要加油", Online = true },
            new Contact { Uin = 20002, Name = "李四", AvatarPath = FriendAvatar, Signature = "Lumia 950 XL", Online = true },
            new Contact { Uin = 20003, Name = "王五", AvatarPath = FriendAvatar, Signature = "在线", Online = true },
            new Contact { Uin = 20004, Name = "老妈", AvatarPath = FriendAvatar, Signature = "", Online = false },
            new Contact { Uin = 20005, Name = "老板", AvatarPath = FriendAvatar, Signature = "勿扰", Online = false },
            new Contact { Uin = 20006, Name = "前端老哥", AvatarPath = FriendAvatar, Signature = "CSS 是门玄学", Online = true },
        };

        private readonly List<FriendRequest> _friendRequests = new List<FriendRequest>
        {
            new FriendRequest { Uin = 30001, Name = "陈同学", AvatarPath = FriendAvatar, Message = "我是你大学同学，加一下" },
            new FriendRequest { Uin = 30002, Name = "Lumia 复活会", AvatarPath = FriendAvatar, Message = "看你也是 WP 钉子户，交个朋友" },
            new FriendRequest { Uin = 30003, Name = "代购小妹", AvatarPath = FriendAvatar, Message = "通过群聊添加" },
        };

        private readonly Dictionary<string, List<GroupMember>> _groupMembers = new Dictionary<string, List<GroupMember>>();

        private readonly List<ChatConversation> _conversations;
        private readonly Dictionary<string, List<ChatMessage>> _messages = new Dictionary<string, List<ChatMessage>>();
        private readonly Random _rng = new Random(20260614);
        private int _idSeed;

        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<TypingState> TypingChanged;

        public MockChatService()
        {
            _conversations = new List<ChatConversation>
            {
                new ChatConversation { Id = "c1", Kind = ConversationKind.Friend, Title = "张三", AvatarPath = FriendAvatar, Preview = "在吗？晚上一起吃饭", LastTime = DateTimeOffset.Now.AddMinutes(-3), Unread = 2, IsPinned = true },
                new ChatConversation { Id = "c2", Kind = ConversationKind.Group, Title = "WP 钉子户交流群", AvatarPath = GroupAvatar, Preview = "李四：Lumia 950 永不为奴", LastTime = DateTimeOffset.Now.AddMinutes(-25), Unread = 9, IsMuted = true },
                new ChatConversation { Id = "c3", Kind = ConversationKind.Friend, Title = "老妈", AvatarPath = FriendAvatar, Preview = "记得多穿点衣服", LastTime = DateTimeOffset.Now.AddHours(-2), Unread = 0 },
                new ChatConversation { Id = "c4", Kind = ConversationKind.Group, Title = "家庭群", AvatarPath = GroupAvatar, Preview = "[图片]", LastTime = DateTimeOffset.Now.AddDays(-1), Unread = 0 },
                new ChatConversation { Id = "c5", Kind = ConversationKind.Friend, Title = "QQ 团队", AvatarPath = FriendAvatar, Preview = "欢迎使用 QQ Reborn", LastTime = DateTimeOffset.Now.AddDays(-3), Unread = 0 },
            };

            SeedMessages("c1", S("张三", "在吗？", false), S("张三", "晚上一起吃饭", false));
            SeedMessages("c2", S("李四", "Lumia 950 永不为奴", false), S("王五", "+1", false), S("我", "哈哈哈", true));
            SeedMessages("c3", S("老妈", "记得多穿点衣服", false));
            SeedMessages("c4", S("妹妹", "周末回家吗", false), SImg("妈妈", "ms-appx:///Assets/Square310x310Logo.scale-200.png", false));
            SeedMessages("c5", S("QQ 团队", "欢迎使用 QQ Reborn", false), S("QQ 团队", "这是一个为 Windows 10 Mobile 打造的第三方 QQ 客户端", false));

            // Group members (fake)
            _groupMembers["c2"] = new List<GroupMember>
            {
                new GroupMember { Uin = 10001, Name = "Jimmy", AvatarPath = SelfAvatar, Role = "群主" },
                new GroupMember { Uin = 20002, Name = "李四", AvatarPath = FriendAvatar, Role = "管理员" },
                new GroupMember { Uin = 20003, Name = "王五", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20007, Name = "WP老炮", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20008, Name = "Lumia930", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20009, Name = "诺基亚情怀", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20010, Name = "Surface党", AvatarPath = FriendAvatar },
            };
            _groupMembers["c4"] = new List<GroupMember>
            {
                new GroupMember { Uin = 10001, Name = "Jimmy", AvatarPath = SelfAvatar, Role = "群主" },
                new GroupMember { Uin = 20004, Name = "老妈", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20011, Name = "老爸", AvatarPath = FriendAvatar },
                new GroupMember { Uin = 20012, Name = "妹妹", AvatarPath = FriendAvatar },
            };

            // A few rich message cards so chats look like real QQ
            AppendExtra("c5", new ChatMessage
            {
                ConversationId = "c5", SenderName = "QQ 团队", SenderUin = 20999, SenderAvatarPath = FriendAvatar,
                Direction = MessageDirection.Incoming, ContentType = MessageContentType.LinkCard, Text = "[链接]",
                LinkTitle = "QQ Reborn —— 为 Win10 Mobile 重生的 QQ", LinkSource = "github.com/qqreborn",
                LinkThumb = "ms-appx:///Assets/Square310x310Logo.scale-200.png",
            });
            AppendExtra("c4", new ChatMessage
            {
                ConversationId = "c4", SenderName = "老爸", SenderUin = 20011, SenderAvatarPath = GroupAvatar,
                Direction = MessageDirection.Incoming, ContentType = MessageContentType.FileMsg, Text = "[文件]",
                FileName = "家庭旅游计划.docx", FileSize = "2.3 MB",
            });
            AppendExtra("c1", new ChatMessage
            {
                ConversationId = "c1", SenderName = "张三", SenderUin = 20001, SenderAvatarPath = FriendAvatar,
                Direction = MessageDirection.Incoming, ContentType = MessageContentType.Location, Text = "[位置]",
                PlaceName = "星巴克(科技园店)", PlaceAddress = "深圳市南山区科技中一路 软件产业基地",
                PlaceThumb = "ms-appx:///Assets/Wide310x150Logo.scale-200.png",
            });
        }

        private void AppendExtra(string convId, ChatMessage m)
        {
            m.Id = NextId();
            m.State = MessageState.Sent;
            if (!_messages.TryGetValue(convId, out var list)) { list = new List<ChatMessage>(); _messages[convId] = list; }
            var baseTime = list.Count > 0 ? list[list.Count - 1].Time : DateTimeOffset.Now.AddMinutes(-5);
            m.Time = baseTime.AddSeconds(30);
            list.Add(m);
        }

        private struct Seed
        {
            public string Sender;
            public string Text;
            public bool Self;
            public string Image;
        }

        private static Seed S(string sender, string text, bool self)
            => new Seed { Sender = sender, Text = text, Self = self };

        private static Seed SImg(string sender, string image, bool self)
            => new Seed { Sender = sender, Text = "[图片]", Self = self, Image = image };

        private void SeedMessages(string convId, params Seed[] items)
        {
            var conv = _conversations.First(c => c.Id == convId);
            var list = new List<ChatMessage>();
            var baseTime = conv.LastTime.AddMinutes(-items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                list.Add(new ChatMessage
                {
                    Id = NextId(),
                    ConversationId = convId,
                    SenderName = item.Sender,
                    SenderUin = item.Self ? _self.Uin : 20000 + i,
                    SenderAvatarPath = item.Self ? SelfAvatar : (conv.Kind == ConversationKind.Group ? GroupAvatar : FriendAvatar),
                    Direction = item.Self ? MessageDirection.Outgoing : MessageDirection.Incoming,
                    ContentType = item.Image != null ? MessageContentType.Image : MessageContentType.Text,
                    Text = item.Text,
                    ImagePath = item.Image,
                    Time = baseTime.AddMinutes(i),
                    State = MessageState.Sent
                });
            }
            _messages[convId] = list;
        }

        private string NextId() => "m" + (++_idSeed);

        public Task<SelfProfile> GetSelfAsync() => Task.FromResult(_self);

        public Task<IReadOnlyList<ChatConversation>> GetConversationsAsync()
        {
            IReadOnlyList<ChatConversation> ordered = _conversations
                .OrderByDescending(c => c.LastTime)
                .ToList();
            return Task.FromResult(ordered);
        }

        public Task<IReadOnlyList<Contact>> GetContactsAsync()
        {
            IReadOnlyList<Contact> ordered = _contacts
                .OrderByDescending(c => c.Online)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(ordered);
        }

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, bool localOnly = false)
        {
            // Mock has no cloud side — localOnly is ignored.
            IReadOnlyList<ChatMessage> result = _messages.TryGetValue(conversationId, out var list)
                ? list.ToList()
                : new List<ChatMessage>();
            return Task.FromResult(result);
        }

        public Task<ChatMessage> SendTextAsync(string conversationId, string text, string mentionsJson = null)
            => SendAsync(conversationId, MessageContentType.Text, text, null, null, 0);

        public Task<ChatMessage> ForwardMessageAsync(string targetConversationId, string messageId)
            => SendAsync(targetConversationId, MessageContentType.Text, "[转发记录]", null, null, 0);

        public Task<ChatMessage> SendImageAsync(string conversationId, string imagePath)
            => SendAsync(conversationId, MessageContentType.Image, "[图片]", imagePath, null, 0);

        public async Task<ChatMessage> SendMixedAsync(string conversationId, string text, IReadOnlyList<string> imagePaths, string replyToMessageId = null, string mentionsJson = null)
        {
            var paths = imagePaths != null
                ? imagePaths.Where(p => !string.IsNullOrEmpty(p)).ToList()
                : new List<string>();
            var hasText = !string.IsNullOrWhiteSpace(text);
            if (paths.Count == 0 && !hasText)
                throw new ArgumentException("empty mixed message");

            if (paths.Count == 0)
                return await SendTextAsync(conversationId, text, mentionsJson);

            if (!hasText && paths.Count == 1)
                return await SendImageAsync(conversationId, paths[0]);

            var preview = hasText ? text.Trim() : ("[图片×" + paths.Count + "]");
            var msg = await SendAsync(conversationId, MessageContentType.Text, preview, paths[0], null, 0);
            if (hasText)
                msg.Elements.Add(new MessageElement { Type = "Text", Text = text.Trim() });
            foreach (var p in paths)
                msg.Elements.Add(new MessageElement { Type = "Image", Url = p });
            msg.ImagePath = paths[0];
            return msg;
        }

        public Task<ChatMessage> SendStickerAsync(string conversationId, string stickerPath)
            => SendAsync(conversationId, MessageContentType.Sticker, "[表情]", stickerPath, null, 0);

        public Task<ChatMessage> SendVoiceAsync(string conversationId, string audioPath, int seconds)
            => SendAsync(conversationId, MessageContentType.Voice, "[语音]", null, audioPath, seconds);

        public Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb)
            => SendAsync(conversationId, MessageContentType.Location, "[位置]", null, null, 0,
                m => { m.PlaceName = placeName; m.PlaceAddress = address; m.PlaceThumb = thumb; });

        public Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string conversationId)
        {
            IReadOnlyList<GroupMember> r = _groupMembers.TryGetValue(conversationId, out var l)
                ? l.ToList() : new List<GroupMember>();
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<FriendRequest>> GetFriendRequestsAsync()
        {
            IReadOnlyList<FriendRequest> r = _friendRequests.ToList();
            return Task.FromResult(r);
        }

        public Task AcceptFriendRequestAsync(FriendRequest request)
        {
            if (request != null)
            {
                request.Handled = true;
                if (!_contacts.Any(c => c.Uin == request.Uin))
                    _contacts.Add(new Contact { Uin = request.Uin, Name = request.Name, AvatarPath = request.AvatarPath, Signature = "", Online = false });
            }
            return Task.CompletedTask;
        }

        public Task SetConversationFlagsAsync(string conversationId, bool? isPinned, bool? isMuted)
        {
            var conv = _conversations.FirstOrDefault(c => c.Id == conversationId);
            if (conv != null)
            {
                if (isPinned.HasValue) conv.IsPinned = isPinned.Value;
                if (isMuted.HasValue) conv.IsMuted = isMuted.Value;
            }
            return Task.CompletedTask;
        }

        private async Task<ChatMessage> SendAsync(string conversationId, MessageContentType type, string preview,
            string imagePath, string audioPath, int seconds, Action<ChatMessage> configure = null)
        {
            var conv = _conversations.FirstOrDefault(c => c.Id == conversationId);
            var msg = new ChatMessage
            {
                Id = NextId(),
                ConversationId = conversationId,
                SenderName = _self.Nickname,
                SenderUin = _self.Uin,
                SenderAvatarPath = SelfAvatar,
                Direction = MessageDirection.Outgoing,
                ContentType = type,
                Text = preview,
                ImagePath = imagePath,
                AudioPath = audioPath,
                VoiceSeconds = seconds,
                Time = DateTimeOffset.Now,
                State = MessageState.Sending
            };
            configure?.Invoke(msg);
            if (!_messages.TryGetValue(conversationId, out var list))
            {
                list = new List<ChatMessage>();
                _messages[conversationId] = list;
            }
            list.Add(msg);

            // Simulate network round-trip.
            await Task.Delay(350);
            msg.State = MessageState.Sent;

            if (conv != null)
            {
                conv.Preview = preview;
                conv.LastTime = msg.Time;
            }

            // Fire a canned auto-reply shortly after.
            _ = AutoReplyAsync(conv, conversationId);
            return msg;
        }

        private async Task AutoReplyAsync(ChatConversation conv, string conversationId)
        {
            await Task.Delay(500 + _rng.Next(400));
            TypingChanged?.Invoke(this, new TypingState { ConversationId = conversationId, IsTyping = true });
            await Task.Delay(900 + _rng.Next(700));
            TypingChanged?.Invoke(this, new TypingState { ConversationId = conversationId, IsTyping = false });
            string[] replies = { "收到~", "好的", "哈哈哈", "这边 QQ Reborn 测试中", "嗯嗯", "稍等" };
            var reply = new ChatMessage
            {
                Id = NextId(),
                ConversationId = conversationId,
                SenderName = conv?.Title ?? "对方",
                SenderUin = 20999,
                SenderAvatarPath = conv != null && conv.Kind == ConversationKind.Group ? GroupAvatar : FriendAvatar,
                Direction = MessageDirection.Incoming,
                Text = replies[_rng.Next(replies.Length)],
                Time = DateTimeOffset.Now,
                State = MessageState.Sent
            };
            if (_messages.TryGetValue(conversationId, out var list))
            {
                // Mark the user's latest outgoing message as read (read receipt).
                var lastOut = list.LastOrDefault(m => m.IsOutgoing);
                if (lastOut != null) lastOut.IsRead = true;
                list.Add(reply);
            }
            if (conv != null)
            {
                conv.Preview = reply.Text;
                conv.LastTime = reply.Time;
            }
            MessageReceived?.Invoke(this, reply);
        }
    }
}
