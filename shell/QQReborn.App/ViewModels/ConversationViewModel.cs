using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public partial class ConversationViewModel : ObservableObject
    {
        private readonly IChatService _chat;
        private static readonly TimeSpan DividerGap = TimeSpan.FromMinutes(5);

        public ConversationViewModel(IChatService chat)
        {
            _chat = chat;
        }

        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();


        public class MentionInfo
        {
            public long Uin { get; set; }
            public string Display { get; set; }
        }

        public System.Collections.Generic.List<MentionInfo> PendingMentions { get; } = new System.Collections.Generic.List<MentionInfo>();

        public ObservableCollection<ChatConversation> ForwardTargets { get; } = new ObservableCollection<ChatConversation>();

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

        private bool _isLoading;
        /// <summary>True while the initial transcript or a fresh remote snapshot is loading.</summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        private string _loadingText = "正在加载消息…";
        public string LoadingText
        {
            get => _loadingText;
            private set => Set(ref _loadingText, value);
        }

        private bool _isSending;
        public bool IsSending
        {
            get => _isSending;
            private set
            {
                if (!Set(ref _isSending, value)) return;
                RaisePropertyChanged(nameof(CanSend));
                RaisePropertyChanged(nameof(SendButtonText));
            }
        }

        public string SendButtonText => IsSending ? "发送中…" : "发送";

        /// <summary>Images staged for the next send (图文混排). Filled by the image picker;
        /// cleared after a successful or failed send attempt that consumed them.</summary>
        public ObservableCollection<string> PendingImages { get; } = new ObservableCollection<string>();

        public bool HasPendingImages => PendingImages.Count > 0;

        public bool CanSend => !IsSending && (!string.IsNullOrWhiteSpace(_draft) || PendingImages.Count > 0);

        public void AttachPendingImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;
            if (PendingImages.Count >= 9) return;
            PendingImages.Add(imagePath);
            RaisePropertyChanged(nameof(HasPendingImages));
            RaisePropertyChanged(nameof(CanSend));
        }

        public void RemovePendingImage(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;
            if (PendingImages.Remove(imagePath))
            {
                RaisePropertyChanged(nameof(HasPendingImages));
                RaisePropertyChanged(nameof(CanSend));
            }
        }

        public void ClearPendingImages()
        {
            if (PendingImages.Count == 0) return;
            PendingImages.Clear();
            RaisePropertyChanged(nameof(HasPendingImages));
            RaisePropertyChanged(nameof(CanSend));
        }

        private bool _isPeerTyping;
        public bool IsPeerTyping { get => _isPeerTyping; set => Set(ref _isPeerTyping, value); }

        // ---- load earlier history ----
        private const int MaxEarlierLoads = 2;
        private int _earlierLoadCount;
        private int _loadGeneration;
        // Guards against a second tap re-entering LoadEarlierAsync while a fetch (mock delay
        // or the remote round-trip) is still in flight -- the strip stays visible/tappable
        // while CanLoadEarlier is true, so without this a fast double-tap would fire two
        // overlapping GetEarlierMessagesAsync calls and could duplicate-insert a batch.
        private bool _loadingEarlier;

        /// <summary>
        /// Whether the "查看更多消息" strip should be shown. For the mock backend this becomes
        /// false once the fabricated history has been loaded MaxEarlierLoads times; for the
        /// remote backend it tracks the server's HasMore flag (see LoadEarlierAsync).
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

        public static string ContentSummary(ChatMessage m)
        {
            return MessagePresentation.GetSummary(m);
        }

        /// <summary>Plain-text export of one bubble (for clipboard / multi-copy).</summary>
        public static string FormatForCopy(ChatMessage m, bool withSender)
        {
            if (m == null || m.IsSystem) return string.Empty;
            var body = m.IsText ? (m.Text ?? string.Empty) : ContentSummary(m);
            if (!withSender) return body;
            var who = m.IsOutgoing
                ? "我"
                : (string.IsNullOrEmpty(m.SenderName) ? "对方" : m.SenderName);
            return who + "：" + body;
        }

        /// <summary>
        /// Peer (or self-from-other-client) recalled a message. With 防撤回 the bubble stays
        /// and is flagged; otherwise it is swapped for a system line.
        /// </summary>
        public void ApplyPeerRecall(string messageId, long napcatMessageId, string senderName, string preview)
        {
            ChatMessage hit = null;
            foreach (var m in Messages)
            {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(messageId) && m.Id == messageId) { hit = m; break; }
                if (napcatMessageId > 0
                    && !string.IsNullOrEmpty(m.Id)
                    && m.Id.EndsWith(":" + napcatMessageId, StringComparison.Ordinal))
                {
                    hit = m;
                    break;
                }
            }

            var who = string.IsNullOrEmpty(senderName)
                ? (hit != null && !string.IsNullOrEmpty(hit.SenderName) ? hit.SenderName : "对方")
                : senderName;

            if (UtilitySettings.AntiRecall && hit != null && !hit.IsSystem)
            {
                hit.IsRevoked = true;
                // Keep original content; mark for the user.
                if (hit.IsText && !string.IsNullOrEmpty(hit.Text)
                    && hit.Text.IndexOf("【已撤回】", StringComparison.Ordinal) < 0)
                {
                    hit.Text = "【已撤回】" + hit.Text;
                }
                if (UtilitySettings.ShowRevokeNotice)
                    AppendSystem(who + " 撤回了一条消息（本地已保留）");
                return;
            }

            if (hit != null)
            {
                var index = Messages.IndexOf(hit);
                if (index >= 0)
                {
                    Messages.RemoveAt(index);
                    if (UtilitySettings.ShowRevokeNotice)
                    {
                        var system = new ChatMessage
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            ConversationId = ConversationId,
                            Direction = MessageDirection.Incoming,
                            ContentType = MessageContentType.System,
                            Text = who + " 撤回了一条消息",
                            Time = hit.Time,
                            ShowTimeDivider = hit.ShowTimeDivider,
                        };
                        Messages.Insert(index, system);
                    }
                    UpdateReadState();
                    return;
                }
            }

            // Message not on screen (already scrolled away / never loaded). Optional tip only.
            if (UtilitySettings.ShowRevokeNotice)
            {
                var tip = who + " 撤回了一条消息";
                if (!string.IsNullOrEmpty(preview) && UtilitySettings.AntiRecall)
                    tip += "：" + preview;
                AppendSystem(tip);
            }
        }

        public void ClearReplyTarget() => ReplyTarget = null;

        /// <summary>
        /// A cached conversation page reuses this view model. Composer state must never cross
        /// into another conversation, otherwise an old draft, attachment, or @ metadata can be
        /// sent to the wrong recipient.
        /// </summary>
        private void ResetComposerForConversationChange()
        {
            Draft = string.Empty;
            ClearPendingImages();
            PendingMentions.Clear();
            ClearReplyTarget();
            IsPeerTyping = false;
        }

        private bool IsCurrentConversation(string conversationId)
        {
            return string.Equals(ConversationId, conversationId, StringComparison.Ordinal);
        }

        private bool IsCurrentLoad(int loadGeneration, string conversationId)
        {
            return _loadGeneration == loadGeneration && IsCurrentConversation(conversationId);
        }

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
            var loadGeneration = ++_loadGeneration;
            IsLoading = true;
            LoadingText = "正在加载消息…";
            if (!IsCurrentConversation(conversationId))
                ResetComposerForConversationChange();

            ConversationId = conversationId;
            Title = title;
            IsGroup = isGroup;

            // Reset "load earlier history" state for the freshly opened conversation.
            // Mock: fabricates history, capped at MaxEarlierLoads.
            // Remote: pages cloud history via getEarlierMessages. Empty chats can still pull
            // (server accepts a null beforeId = newest page); keep the strip available until
            // the server reports hasMore=false.
            _earlierLoadCount = 0;
            _loadingEarlier = false;
            CanLoadEarlier = true;

            // Clear whatever is on screen NOW, before the await: it's stale by definition --
            // either a previous conversation's list, or (forward re-entry from the main list
            // to the SAME cached conversation) an outdated copy of this one that misses
            // everything that arrived while the page sat unsubscribed on the back stack.
            // Keeping it would feed the whole old list into the carried-over merge below,
            // whose "carried-over is newer than the snapshot" ordering assumption only holds
            // for pushes that race in DURING the fetch -- fresh arrivals would then sort
            // above the old history instead of at the bottom.
            // The clear runs in the synchronous prefix of this method (same UI-thread slice
            // as the page's event subscribe), so no push can land between subscribe and here;
            // anything OnIncoming() appends while GetMessagesAsync is in flight lands AFTER
            // this clear and is reconciled by BuildCarriedOver once the snapshot returns.
            Messages.Clear();

            var localMsgs = await MessageCache.LoadMessagesAsync(conversationId);
            if (!IsCurrentLoad(loadGeneration, conversationId)) return;
            bool needFetch = true;
            if (localMsgs.Count > 0 && Conversation != null)
            {
                // Compare with server's LastTime (updated when Conversations are loaded)
                if (localMsgs[localMsgs.Count - 1].Time >= Conversation.LastTime)
                {
                    needFetch = false;
                }
            }

            System.Collections.Generic.IReadOnlyList<ChatMessage> msgs = localMsgs;
            
            if (needFetch)
            {
                LoadingText = localMsgs.Count > 0 ? "正在同步最新消息…" : "正在从网关加载消息…";
                try
                {
                    msgs = await _chat.GetMessagesAsync(conversationId);
                }
                catch (Exception)
                {
                    if (!IsCurrentLoad(loadGeneration, conversationId)) return;
                    // Remote backend unreachable/timed out. Keep whatever raced in live for this
                    // conversation while we awaited (if anything) and surface a visible system
                    // notice instead of letting the exception escape into the page's async-void
                    // OnNavigatedTo and kill the app.
                    var kept = BuildCarriedOver(conversationId);
                    Messages.Clear();
                    ChatMessage prevOnError = null;
                    foreach (var m in kept)
                    {
                        ApplyDivider(prevOnError, m);
                        Messages.Add(m);
                        prevOnError = m;
                    }
                    UpdateReadState();
                    AppendSystem("无法加载消息记录（服务器未连接）");
                    if (Stickers.Count == 0) await LoadStickersAsync();
                    if (!IsCurrentLoad(loadGeneration, conversationId)) return;
                    // Keep the strip so the user can retry once the bridge is back.
                    CanLoadEarlier = _chat is IGatewayService || _chat is MockChatService;
                    IsLoading = false;
                    return;
                }
            }

            if (!IsCurrentLoad(loadGeneration, conversationId)) return;

            // Snapshot AFTER the await returns: Messages was cleared above, so anything in it
            // now is a live OnIncoming() push that arrived for conversationId while
            // GetMessagesAsync was in flight. BuildCarriedOver's conversation filter is
            // belt-and-braces on top of OnIncoming's own ConversationId check.
            var carriedOver = BuildCarriedOver(conversationId);

            var liveIds = new System.Collections.Generic.HashSet<string>();
            foreach (var existing in carriedOver)
                if (!string.IsNullOrEmpty(existing.Id)) liveIds.Add(existing.Id);

            var merged = new System.Collections.Generic.List<ChatMessage>(msgs.Count + carriedOver.Count);
            foreach (var m in msgs)
            {
                if (!string.IsNullOrEmpty(m.Id) && liveIds.Contains(m.Id)) continue; // already have it live
                merged.Add(m);
            }
            // Anything that arrived live during the fetch is newer than the snapshot in
            // practice (the snapshot was requested first), so it belongs after it.
            merged.AddRange(carriedOver);

            // If the list is already equivalent (same ids/order), skip Clear()+rebuild so
            // the chat ListView does not jump while the user is scrolling history.
            bool same = Messages.Count == merged.Count;
            if (same)
            {
                for (int i = 0; i < merged.Count; i++)
                {
                    var a = Messages[i];
                    var b = merged[i];
                    if (a == null || b == null || a.Id != b.Id)
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (!same)
            {
                Messages.Clear();
                ChatMessage prev = null;
                foreach (var m in merged)
                {
                    ApplyDivider(prev, m);
                    Messages.Add(m);
                    prev = m;
                }
            }

            UpdateReadState();
            _ = MessageCache.SaveMessagesAsync(conversationId, Messages);

            // Remote: always offer "查看更多消息" after open -- getMessages may already have
            // cloud-backfilled a first page, and further taps page older. Server hasMore will
            // collapse the strip when history is exhausted. Mock keeps the demo counter path.
            CanLoadEarlier = true;

            if (Stickers.Count == 0) await LoadStickersAsync();
            IsLoading = false;
        }

        /// <summary>
        /// Messages currently in the collection that belong to targetConversationId -- i.e.
        /// messages that raced in live via OnIncoming() while LoadAsync's GetMessagesAsync
        /// await was in flight. Anything belonging to a different conversation is a leftover
        /// from this VM's previous conversation (it's reused across navigations) and must be
        /// dropped rather than carried into the new one.
        /// </summary>
        private System.Collections.Generic.List<ChatMessage> BuildCarriedOver(string targetConversationId)
        {
            var kept = new System.Collections.Generic.List<ChatMessage>();
            foreach (var existing in Messages)
                if (existing.ConversationId == targetConversationId) kept.Add(existing);
            return kept;
        }

        public async Task LoadAsync(ChatConversation conv)
        {
            Conversation = conv;
            if (conv == null) return;
            await LoadAsync(conv.Id, conv.Title, conv.IsGroup);
        }

        /// <summary>
        /// "查看更多消息": pages older messages into the TOP of the list. Against the mock
        /// backend this fabricates a canned batch (unchanged demo behavior); against a
        /// remote backend it fetches real history via RemoteChatService.GetEarlierMessagesAsync.
        /// </summary>
    }
}
