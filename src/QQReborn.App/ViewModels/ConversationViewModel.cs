using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public class ConversationViewModel : ObservableObject
    {
        private readonly IChatService _chat;
        private static readonly TimeSpan DividerGap = TimeSpan.FromMinutes(5);

        public ConversationViewModel(IChatService chat)
        {
            _chat = chat;
        }

        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        /// <summary>Classic QQ emoticon sticker asset paths.</summary>
        public ObservableCollection<string> Stickers { get; } = new ObservableCollection<string>();

        public string ConversationId { get; private set; }

        /// <summary>The conversation this VM was loaded for; kept for group-info navigation.</summary>
        public ChatConversation Conversation { get; private set; }

        private string _title;
        public string Title { get => _title; set => Set(ref _title, value); }

        private bool _isGroup;
        public bool IsGroup { get => _isGroup; set => Set(ref _isGroup, value); }

        private string _draft;
        public string Draft
        {
            get => _draft;
            set { if (Set(ref _draft, value)) RaisePropertyChanged(nameof(CanSend)); }
        }

        public bool CanSend => !string.IsNullOrWhiteSpace(_draft);

        private bool _isPeerTyping;
        public bool IsPeerTyping { get => _isPeerTyping; set => Set(ref _isPeerTyping, value); }

        // ---- load earlier history ----
        private const int MaxEarlierLoads = 2;
        private int _earlierLoadCount;

        /// <summary>
        /// Whether the "查看更多消息" strip should be shown. Becomes false once the
        /// (fake) history has been loaded MaxEarlierLoads times.
        /// </summary>
        private bool _canLoadEarlier = true;
        public bool CanLoadEarlier { get => _canLoadEarlier; set => Set(ref _canLoadEarlier, value); }

        // ---- reply quote ----
        private ChatMessage _replyTarget;
        public ChatMessage ReplyTarget
        {
            get => _replyTarget;
            set
            {
                if (Set(ref _replyTarget, value))
                {
                    RaisePropertyChanged(nameof(HasReplyTarget));
                    RaisePropertyChanged(nameof(ReplyTargetPreview));
                }
            }
        }

        public bool HasReplyTarget => _replyTarget != null;

        /// <summary>"sender：text" preview of the message currently being replied to.</summary>
        public string ReplyTargetPreview
        {
            get
            {
                if (_replyTarget == null) return string.Empty;
                var who = string.IsNullOrEmpty(_replyTarget.SenderName) ? "我" : _replyTarget.SenderName;
                var what = _replyTarget.IsText ? _replyTarget.Text : ContentSummary(_replyTarget);
                return who + "：" + what;
            }
        }

        private static string ContentSummary(ChatMessage m)
        {
            if (m == null) return string.Empty;
            if (m.IsImage) return "[图片]";
            if (m.IsSticker) return "[表情]";
            if (m.IsVoice) return "[语音]";
            if (m.IsLinkCard) return "[链接]";
            if (m.IsFile) return "[文件]";
            if (m.IsLocation) return "[位置]";
            return m.Text ?? string.Empty;
        }

        public void ClearReplyTarget() => ReplyTarget = null;

        /// <summary>Common emoji for the picker panel.</summary>
        public string[] Emojis { get; } =
        {
            "😀","😁","😂","🤣","😊","😍","😘","😎","🤔","😅",
            "😭","😡","😱","🥰","😴","🤤","🙄","😬","😇","🤗",
            "👍","👎","👌","🙏","💪","👏","🎉","❤️","💔","🔥",
            "🐧","🌹","☀️","🌙","⭐","✅","❌","💯","🎂","🍺"
        };

        public Task LoadAsync(string conversationId, string title)
        {
            return LoadAsync(conversationId, title, false);
        }

        public async Task LoadAsync(string conversationId, string title, bool isGroup)
        {
            ConversationId = conversationId;
            Title = title;
            IsGroup = isGroup;
            ReplyTarget = null;

            // Reset "load earlier history" state for the freshly opened conversation.
            _earlierLoadCount = 0;
            CanLoadEarlier = true;

            Messages.Clear();
            var msgs = await _chat.GetMessagesAsync(conversationId);
            ChatMessage prev = null;
            foreach (var m in msgs)
            {
                ApplyDivider(prev, m);
                Messages.Add(m);
                prev = m;
            }

            UpdateReadState();

            if (Stickers.Count == 0) await LoadStickersAsync();
        }

        public async Task LoadAsync(ChatConversation conv)
        {
            Conversation = conv;
            if (conv == null) return;
            await LoadAsync(conv.Id, conv.Title, conv.IsGroup);
        }

        /// <summary>
        /// Prepend a small batch of fabricated, older messages to the TOP of the list,
        /// simulating "查看更多消息" history paging. Limited to MaxEarlierLoads batches.
        /// </summary>
        public Task LoadEarlierAsync()
        {
            if (!CanLoadEarlier) return Task.CompletedTask;

            // Anchor before the oldest message currently shown so new ones land earlier.
            var oldest = Messages.Count > 0 ? Messages[0].Time : DateTimeOffset.Now;
            var baseTime = oldest.AddMinutes(-(10 * (_earlierLoadCount + 1)) - 5);

            const string selfAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
            const string peerAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
            var peerName = IsGroup ? "群友" : (string.IsNullOrEmpty(Title) ? "对方" : Title);

            // Canned chat lines (alternating peer / self), oldest first.
            var lines = new[] { "在干嘛呢？", "刚下班，你呢", "我也刚到家", "晚点一起开黑？", "好啊，叫上他们" };

            var batch = new System.Collections.Generic.List<ChatMessage>();
            for (int i = 0; i < lines.Length; i++)
            {
                var incoming = (i % 2) == 0; // start with peer
                batch.Add(new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = ConversationId,
                    Direction = incoming ? MessageDirection.Incoming : MessageDirection.Outgoing,
                    ContentType = MessageContentType.Text,
                    Text = lines[i],
                    SenderName = incoming ? peerName : "我",
                    SenderAvatarPath = incoming ? peerAvatar : selfAvatar,
                    Time = baseTime.AddSeconds(i * 30),
                    State = MessageState.Sent
                });
            }

            // Insert the batch at the top, preserving its internal order.
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                Messages.Insert(0, batch[i]);
            }

            // Time dividers depend on neighbours, so recompute across the whole list.
            RecomputeDividers();
            UpdateReadState();

            _earlierLoadCount++;
            if (_earlierLoadCount >= MaxEarlierLoads) CanLoadEarlier = false;

            return Task.CompletedTask;
        }

        /// <summary>Recompute the time-divider flags for every message in order.</summary>
        private void RecomputeDividers()
        {
            ChatMessage prev = null;
            foreach (var m in Messages)
            {
                ApplyDivider(prev, m);
                prev = m;
            }
        }

        private async Task LoadStickersAsync()
        {
            try
            {
                var assets = await Package.Current.InstalledLocation.GetFolderAsync("Assets");
                var folder = await assets.GetFolderAsync("Emoticons");
                var files = await folder.GetFilesAsync();
                foreach (var f in files)
                {
                    Stickers.Add("ms-appx:///Assets/Emoticons/" + f.Name);
                }
            }
            catch
            {
                // No stickers bundled; panel stays empty.
            }
        }

        public void InsertEmoji(string emoji) => Draft = (_draft ?? string.Empty) + emoji;

        public void DeleteMessage(ChatMessage msg)
        {
            if (msg != null) Messages.Remove(msg);
        }

        /// <summary>Recall an outgoing message: swap it for a centered system line at the same spot.</summary>
        public void RecallMessage(ChatMessage msg)
        {
            if (msg == null) return;
            var index = Messages.IndexOf(msg);
            if (index < 0) return;

            var system = new ChatMessage
            {
                // Fresh id so a later server echo/forward keyed by the original id can't collide.
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = msg.ConversationId,
                Direction = MessageDirection.Outgoing,
                ContentType = MessageContentType.System,
                Text = "你撤回了一条消息",
                Time = msg.Time,
                ShowTimeDivider = msg.ShowTimeDivider
            };

            Messages.RemoveAt(index);
            Messages.Insert(index, system);
            UpdateReadState();
        }

        public async Task SendAsync()
        {
            if (!CanSend) return;
            var text = _draft.Trim();
            // Clear optimistically for a snappy UI, but restore the draft if the send fails
            // (e.g. the remote backend timed out / dropped) so the user doesn't lose typing.
            Draft = string.Empty;
            try
            {
                var sent = await _chat.SendTextAsync(ConversationId, text);
                ApplyReplyTarget(sent);
                Append(sent);
            }
            catch (Exception)
            {
                if (string.IsNullOrEmpty(_draft)) Draft = text;
            }
        }

        public async Task SendImageAsync(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;
            var sent = await _chat.SendImageAsync(ConversationId, imagePath);
            ApplyReplyTarget(sent);
            Append(sent);
        }

        public async Task SendStickerAsync(string stickerPath)
        {
            if (string.IsNullOrEmpty(stickerPath)) return;
            var sent = await _chat.SendStickerAsync(ConversationId, stickerPath);
            ApplyReplyTarget(sent);
            Append(sent);
        }

        public async Task SendVoiceAsync(string audioPath, int seconds)
        {
            if (string.IsNullOrEmpty(audioPath)) return;
            var sent = await _chat.SendVoiceAsync(ConversationId, audioPath, seconds);
            ApplyReplyTarget(sent);
            Append(sent);
        }

        public async Task SendLocationAsync(string placeName, string address, string thumb)
        {
            var sent = await _chat.SendLocationAsync(ConversationId, placeName, address, thumb);
            ApplyReplyTarget(sent);
            Append(sent);
        }

        /// <summary>Append a local system notice (撤回 / 戳一戳 / etc).</summary>
        public void AppendSystem(string text)
        {
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = ConversationId,
                Direction = MessageDirection.Outgoing,
                ContentType = MessageContentType.System,
                Text = text,
                Time = DateTimeOffset.Now
            };
            Append(msg);
        }

        /// <summary>Stamp the reply quote on a freshly-sent message from the current reply target.</summary>
        private void ApplyReplyTarget(ChatMessage sent)
        {
            if (sent == null || _replyTarget == null) return;
            sent.ReplyToSender = string.IsNullOrEmpty(_replyTarget.SenderName) ? "我" : _replyTarget.SenderName;
            sent.ReplyToText = _replyTarget.Text;
            ReplyTarget = null;
        }

        /// <summary>Append a message produced by a forward action that targets this conversation.</summary>
        public void AppendForwarded(ChatMessage msg) => Append(msg);

        /// <summary>Called on the UI thread by the page when a message arrives for this conversation.</summary>
        public void OnIncoming(ChatMessage msg)
        {
            if (msg == null || msg.ConversationId != ConversationId) return;
            Append(msg);
            UpdateReadState();
        }

        private void Append(ChatMessage msg)
        {
            if (msg == null || Messages.Contains(msg)) return;
            var prev = Messages.Count > 0 ? Messages[Messages.Count - 1] : null;
            ApplyDivider(prev, msg);
            Messages.Add(msg);
            UpdateReadState();
        }

        /// <summary>Show the read-receipt label only on the latest outgoing (non-system) message.</summary>
        private void UpdateReadState()
        {
            ChatMessage latestOutgoing = null;
            foreach (var m in Messages)
            {
                m.ShowReadState = false;
                if (m.IsOutgoing && !m.IsSystem) latestOutgoing = m;
            }
            if (latestOutgoing != null) latestOutgoing.ShowReadState = true;
        }

        private static void ApplyDivider(ChatMessage prev, ChatMessage msg)
        {
            msg.ShowTimeDivider = prev == null || (msg.Time - prev.Time) > DividerGap;
        }
    }
}
