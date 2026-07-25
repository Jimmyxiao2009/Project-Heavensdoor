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

        /// <summary>Images staged for the next send (图文混排). Filled by the image picker;
        /// cleared after a successful or failed send attempt that consumed them.</summary>
        public ObservableCollection<string> PendingImages { get; } = new ObservableCollection<string>();

        public bool HasPendingImages => PendingImages.Count > 0;

        public bool CanSend => !string.IsNullOrWhiteSpace(_draft) || PendingImages.Count > 0;

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
            if (m == null) return string.Empty;
            if (m.IsImage) return "[图片]";
            if (m.IsSticker) return "[表情]";
            if (m.IsVoice) return "[语音]";
            if (m.IsVideo) return "[视频]";
            if (m.IsLinkCard) return "[链接]";
            if (m.IsFile) return "[文件]";
            if (m.IsLocation) return "[位置]";
            return m.Text ?? string.Empty;
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
                try
                {
                    msgs = await _chat.GetMessagesAsync(conversationId);
                }
                catch (Exception)
                {
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
                    // Keep the strip so the user can retry once the bridge is back.
                    CanLoadEarlier = _chat is RemoteChatService || _chat is MockChatService;
                    return;
                }
            }

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
        public async Task LoadEarlierAsync()
        {
            if (!CanLoadEarlier || _loadingEarlier) return;
            _loadingEarlier = true;
            try
            {
                if (_chat is RemoteChatService remote) await LoadEarlierRemoteAsync(remote);
                else await LoadEarlierMockAsync();
            }
            finally { _loadingEarlier = false; }
        }

        /// <summary>
        /// Prepend a small batch of fabricated, older messages to the TOP of the list,
        /// simulating "查看更多消息" history paging. Limited to MaxEarlierLoads batches.
        /// </summary>
        private Task LoadEarlierMockAsync()
        {
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

        /// <summary>
        /// Real backfill against a remote backend: anchors on the oldest REAL message
        /// currently on screen (skipping system notices -- 撤回/戳一戳 lines aren't part of
        /// the server's history and have no id the server would recognize), fetches one page
        /// older, de-dupes against what's already shown, and prepends it.
        /// </summary>
        private async Task LoadEarlierRemoteAsync(RemoteChatService remote)
        {
            // Prefer the oldest real message as the page-before anchor. If the chat is empty
            // (or only system notices), pass null so the server pulls the newest cloud page
            // (friend roam from "now" / group FetchGroupExtra + recent sequences).
            var anchor = FirstRealMessage();
            var beforeId = anchor != null ? anchor.Id : null;

            EarlierMessagesResult result;
            try
            {
                result = await remote.GetEarlierMessagesAsync(ConversationId, beforeId, 20);
            }
            catch (Exception ex)
            {
                // Keep the strip up so the user can retry (transient network blip); nothing
                // in Messages changed, so there's nothing to roll back.
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("加载历史消息失败（" + detail + "）");
                return;
            }

            var older = result != null ? result.Messages : null;
            if (older == null || older.Count == 0)
            {
                // Empty page: either no cloud history, or we've hit the beginning.
                if (!HasAnyRealMessage())
                    AppendSystem("暂无更多云端历史消息");
                CanLoadEarlier = false;
                return;
            }

            // Server contract: Messages is oldest-first. De-dupe against everything already
            // shown (not just the anchor) -- a retry after a partial failure, or a page whose
            // window overlaps what's already on screen, could otherwise double-insert.
            var existingIds = new System.Collections.Generic.HashSet<string>();
            foreach (var existing in Messages)
                if (!string.IsNullOrEmpty(existing.Id)) existingIds.Add(existing.Id);

            var toInsert = new System.Collections.Generic.List<ChatMessage>();
            foreach (var m in older)
            {
                if (!string.IsNullOrEmpty(m.Id) && existingIds.Contains(m.Id)) continue;
                toInsert.Add(m);
            }

            if (toInsert.Count > 0)
            {
                // Insert at the top, preserving oldest-first order (batch[0] is oldest).
                // When the pull was unanchored and Messages was empty, this just fills the
                // list; when paging older, they land above the previous first item.
                for (int i = toInsert.Count - 1; i >= 0; i--)
                    Messages.Insert(0, toInsert[i]);

                // Dividers depend on neighbours; the prepended batch changes what's above the
                // messages that used to be at the top, so recompute across the whole list
                // rather than trying to patch just the seam.
                RecomputeDividers();
                UpdateReadState();
            }

            // Keep paging while the server says there is more OR we actually inserted new
            // rows (a full page of dups still sets HasMore so the next tap re-anchors older).
            // Only collapse when the page is empty or HasMore is false.
            if (toInsert.Count == 0 && !result.HasMore)
                CanLoadEarlier = false;
            else
                CanLoadEarlier = result.HasMore || toInsert.Count > 0;
        }

        /// <summary>Oldest message currently shown that represents real conversation content
        /// -- i.e. not a local system notice (撤回/戳一戳/连接失败 lines), which never came
        /// from the server and has no id the server's history endpoint would recognize.</summary>
        private ChatMessage FirstRealMessage()
        {
            foreach (var m in Messages)
                if (!m.IsSystem) return m;
            return null;
        }

        private bool HasAnyRealMessage() => FirstRealMessage() != null;

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

        /// <summary>
        /// Recall an outgoing message. Against the mock backend this stays a purely local,
        /// always-succeeds swap (unchanged demo behavior). Against a remote backend it first
        /// asks the server via RemoteChatService.RecallMessageAsync -- only past the recall
        /// time window or otherwise unsupported by the account -- and only mutates the UI on
        /// success, so a failed recall leaves the original bubble intact instead of lying to
        /// the user (and to the peer, who never saw a recall on the wire either).
        /// Called from an async void context (right-click menu handler), so this method
        /// itself must never let an exception escape.
        /// </summary>
        public async Task RecallMessageAsync(ChatMessage msg)
        {
            if (msg == null) return;
            if (_chat is RemoteChatService remote)
            {
                bool recalled;
                try
                {
                    recalled = await remote.RecallMessageAsync(ConversationId, msg.Id);
                }
                catch (Exception)
                {
                    recalled = false;
                }
                if (!recalled)
                {
                    AppendSystem("撤回失败（超过时限或不支持）");
                    return;
                }
            }
            ApplyLocalRecall(msg);
        }

        /// <summary>Swap a message for a centered "你撤回了一条消息" system line at the same spot.
        /// Pure local UI mutation -- callers decide whether the backend has already confirmed
        /// the recall (see RecallMessageAsync).</summary>
        private void ApplyLocalRecall(ChatMessage msg)
        {
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
            var text = (_draft ?? string.Empty).Trim();
            var images = new System.Collections.Generic.List<string>(PendingImages);
            var replyTarget = _replyTarget;
            // Clear optimistically for a snappy UI, but restore draft/images if the send fails
            // (e.g. the remote backend timed out / dropped) so the user doesn't lose typing.
            Draft = string.Empty;
            ClearPendingImages();

            string mentionsJson = null;
            if (PendingMentions.Count > 0)
            {
                var arr = new Windows.Data.Json.JsonArray();
                foreach (var m in PendingMentions)
                {
                    var o = new Windows.Data.Json.JsonObject();
                    o["uin"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.Uin);
                    o["display"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Display);
                    arr.Add(o);
                }
                mentionsJson = arr.Stringify();
                PendingMentions.Clear();
            }

            try
            {
                ChatMessage sent;
                if (images.Count > 0)
                {
                    // One protocol message: optional caption + 1..N images (图文混排).
                    sent = await _chat.SendMixedAsync(
                        ConversationId,
                        text,
                        images,
                        replyTarget != null ? replyTarget.Id : null,
                        mentionsJson);
                }
                else if (_chat is RemoteChatService remote && replyTarget != null)
                {
                    // Against a real/remote backend, embed an actual reply-quote in the protocol
                    // message (so the recipient's real client sees it and it survives a reload)
                    // instead of just stamping it on our own local copy after the fact.
                    sent = await remote.SendTextWithReplyAsync(ConversationId, text, replyTarget.Id, mentionsJson);
                }
                else
                {
                    sent = await _chat.SendTextAsync(ConversationId, text, mentionsJson);
                }
                ApplyReplyTarget(sent, replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(_draft)) Draft = text;
                if (images.Count > 0 && PendingImages.Count == 0)
                {
                    foreach (var p in images) AttachPendingImage(p);
                }
                // Surface the real reason (e.g. sign 401 / seq=0 / not-online) instead of a
                // silent draft restore that looks like "can't send to groups".
                var detail = string.IsNullOrEmpty(ex.Message)
                    ? "请稍后重试"
                    : Services.RemoteChatService.FormatSocketError("发送失败", ex);
                // Avoid double prefix when FormatSocketError already includes it.
                if (detail.StartsWith("发送失败", System.StringComparison.Ordinal))
                    AppendSystem(detail);
                else
                    AppendSystem("发送失败（" + detail + "）");
            }
        }

        // The media send paths must not let a backend failure escape: their callers are
        // async void page event handlers, and an unhandled exception there kills the app
        // (the real server rejects image/sticker/voice with unsupported-content for now).
        public async Task SendImageAsync(string imagePath)
        {
            // Stage for 图文混排 instead of immediately sending a bare image message.
            // User can type a caption and press 发送; pure-image still works with empty draft.
            AttachPendingImage(imagePath);
            await Task.CompletedTask;
        }

        public async Task SendStickerAsync(string stickerPath)
        {
            if (string.IsNullOrEmpty(stickerPath)) return;
            try
            {
                var sent = await _chat.SendStickerAsync(ConversationId, stickerPath);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("发送失败：表情未发出（" + detail + "）");
            }
        }

        public async Task SendVoiceAsync(string audioPath, int seconds)
        {
            if (string.IsNullOrEmpty(audioPath)) return;
            try
            {
                var sent = await _chat.SendVoiceAsync(ConversationId, audioPath, seconds);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("发送失败：语音未发出（" + detail + "）");
            }
        }

        public async Task SendLocationAsync(string placeName, string address, string thumb)
        {
            try
            {
                var sent = await _chat.SendLocationAsync(ConversationId, placeName, address, thumb);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception) { AppendSystem("发送失败：位置没有发出去"); }
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

        /// <summary>
        /// Stamp the reply quote on a freshly-sent message. Takes the reply target that was
        /// CAPTURED before the send await (not the live field): the user can retarget or
        /// clear the reply banner while the send is in flight, and the bubble must reflect
        /// what actually went out on the wire. When the backend already returned reply
        /// metadata (RealServer echoes replyToSender/replyToText), keep the authoritative
        /// server values instead of clobbering them with a local guess -- otherwise the
        /// in-session bubble and the reloaded-from-server one would render differently.
        /// </summary>
        private void ApplyReplyTarget(ChatMessage sent, ChatMessage replyTarget)
        {
            if (sent == null || replyTarget == null) return;
            if (string.IsNullOrEmpty(sent.ReplyToSender))
            {
                sent.ReplyToSender = string.IsNullOrEmpty(replyTarget.SenderName) ? "我" : replyTarget.SenderName;
                sent.ReplyToText = replyTarget.Text;
            }
            // Only clear the pending banner if the user hasn't picked a new target mid-send.
            if (ReferenceEquals(_replyTarget, replyTarget)) ReplyTarget = null;
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
            // Same wire message can arrive twice as two distinct objects: once from the
            // send-response parse and once from the messageReceived broadcast (the server
            // can only fully prevent that on its side when the echo loses the race).
            if (!string.IsNullOrEmpty(msg.Id))
            {
                foreach (var existing in Messages)
                    if (existing.Id == msg.Id) return;
            }
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
