using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly IChatService _chat;

        public MainViewModel(IChatService chat)
        {
            _chat = chat;
        }

        public ObservableCollection<ChatConversation> Conversations { get; } = new ObservableCollection<ChatConversation>();

        public ObservableCollection<Contact> Contacts { get; } = new ObservableCollection<Contact>();

        private SelfProfile _self;
        public SelfProfile Self { get => _self; private set => Set(ref _self, value); }

        private bool _isLoaded;
        public bool IsLoaded => _isLoaded;

        // Set by Detach() and checked after every await in LoadAsync/RefreshConversationsAsync
        // so a continuation that resumes after the page has already navigated away doesn't
        // touch the (possibly-shared/singleton-backed) collections or re-subscribe to events
        // that Detach() already tore down. MainPage/MainViewModel are recreated on every
        // back-navigation, so this race is routine, not exceptional.
        private bool _detached;

        // Single-flight guard for the "unknown conversation" catch-up fetch triggered from
        // OnMessageReceived (see below) -- a burst of messages from a brand-new conversation
        // should not fan out into N concurrent GetConversationsAsync calls.
        private bool _refreshing;

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            // Render the last known list immediately. The live fetch below still owns
            // correctness and merges fresh unread/pin/mute values in the background.
            try
            {
                var cached = await ConversationCache.LoadAsync();
                if (!_detached && cached != null && cached.Count > 0)
                {
                    foreach (var c in cached)
                    {
                        UnreadBadgeStore.SetAtLeast(c.Id, c.Unread);
                        UnreadBadgeStore.ApplyTo(c);
                        Conversations.Add(c);
                    }
                    ResortConversations();
                }
            }
            catch { /* cache is only a fast first paint */ }

            // Subscribe before the first await: OnNavigatedFrom -> Detach() can run while
            // this method is suspended awaiting the backend, and if the subscribe happened
            // only at the end, Detach()'s -= would run first (no-op, not yet subscribed)
            // and this continuation's += would run after, leaving a zombie VM permanently
            // subscribed to the app-lifetime chat service singleton. Subscribing up front
            // means Detach() always has something real to unsubscribe, and the _detached
            // check below covers the "don't touch collections after detach" half.
            // -= before += keeps a retried LoadAsync (failure path below resets _isLoaded)
            // from stacking a second subscription on the same VM instance. Also clear
            // _detached: a retry after a failed load means we're back in a live/attached
            // state (OnNavigatedTo just called us again), so a stale flag from an earlier
            // Detach() on this same instance must not suppress this attempt.
            _detached = false;
            _chat.MessageReceived -= OnMessageReceived;
            _chat.MessageReceived += OnMessageReceived;
            if (_chat is RemoteChatService remote)
            {
                remote.Reconnected -= OnReconnected;
                remote.Reconnected += OnReconnected;
            }

            try
            {
                // NapCat path: bind the PC-side logged-in account so conversation lists
                // and send work without requiring a separate "登录 QQ" QR step.
                if (_chat is RemoteChatService remoteBind)
                {
                    try { await remoteBind.ConfigureAccountAsync("", "", ""); }
                    catch { /* surfaced on getSelf if still broken */ }
                }

                var self = await _chat.GetSelfAsync();
                if (_detached) return;
                Self = self;

                var convs = await _chat.GetConversationsAsync();
                if (_detached) return;
                MergeConversations(convs);
                _ = ConversationCache.SaveAsync(Conversations);

                var contacts = await _chat.GetContactsAsync();
                if (_detached) return;
                Contacts.Clear();
                foreach (var c in contacts) Contacts.Add(c);
            }
            catch (System.Exception)
            {
                // Remote backend unreachable (server not running / wrong host). This is the
                // app's very first await after launch -- letting it escape the async void
                // OnNavigatedTo would kill the process. Show empty lists instead; the user
                // can fix the server address in Settings and come back.
                _isLoaded = false;
            }
        }

        /// <summary>Detach from the app-lifetime chat service. MainPage is recreated on every
        /// back-navigation, so without this every visit would leave one more zombie handler
        /// (and its whole page/VM object graph) hanging off the singleton's event. Idempotent --
        /// safe to call more than once (OnNavigatedFrom is the only caller today, but a stray
        /// double-call must not throw).</summary>
        public void Attach()
        {
            _detached = false;
            _chat.MessageReceived -= OnMessageReceived;
            _chat.MessageReceived += OnMessageReceived;
            if (_chat is RemoteChatService remote)
            {
                remote.Reconnected -= OnReconnected;
                remote.Reconnected += OnReconnected;
            }
        }

        public void Detach()
        {
            _detached = true;
            _chat.MessageReceived -= OnMessageReceived;
            if (_chat is RemoteChatService remote) remote.Reconnected -= OnReconnected;
        }

        private async void OnMessageReceived(object sender, ChatMessage msg)
        {
            if (_detached) return; // App-level UnreadBadgeStore still counted; list not bound.

            // Bump the conversation's preview/time/unread; the bound properties raise change notifications.
            foreach (var c in Conversations)
            {
                if (c.Id == msg.ConversationId)
                {
                    c.Preview = PreviewFor(msg);
                    c.LastTime = msg.Time;
                    // Don't accrue an unread badge for the conversation the user is
                    // currently reading, nor for our own messages echoed back from
                    // another device (the real bridge pushes those as Outgoing).
                    // UnreadBadgeStore already +1'd for non-active; mirror into the row.
                    if (msg.ConversationId != App.ActiveConversationId && msg.Direction != MessageDirection.Outgoing)
                        UnreadBadgeStore.ApplyTo(c);
                    ResortConversations();
                    return;
                }
            }

            // Unknown conversation: a real backend can push the first message from a brand
            // new friend/group before GetConversationsAsync ever knew about it. Try a
            // catch-up fetch+merge first (single-flighted); if the conversation still isn't
            // there afterwards (e.g. MockChatService, which never grows new conversations),
            // synthesize a minimal row from the message itself so the row (and the in-app
            // banner, which needs a matching conversation to fire) shows up regardless.
            await RefreshConversationsAsync();
            if (_detached) return;

            if (!Conversations.Any(c => c.Id == msg.ConversationId))
            {
                var synthesized = new ChatConversation
                {
                    Id = msg.ConversationId,
                    Kind = !string.IsNullOrEmpty(msg.ConversationId) && msg.ConversationId[0] == 'g'
                        ? ConversationKind.Group : ConversationKind.Friend,
                    Title = msg.SenderName,
                    AvatarPath = msg.SenderAvatarPath,
                    Preview = PreviewFor(msg),
                    LastTime = msg.Time,
                    Unread = 0,
                };
                UnreadBadgeStore.ApplyTo(synthesized);
                Conversations.Add(synthesized);
                ResortConversations();
            }
        }

        /// <summary>Raised (on the UI thread) when RemoteChatService's auto-reconnect loop
        /// re-establishes a dropped connection. Re-pulls the conversation list to pick up
        /// anything that arrived while disconnected; failures are swallowed so a still-flaky
        /// connection doesn't crash the app -- the next successful reconnect (or the next
        /// pull-to-refresh, if one exists) will catch up.</summary>
        private async void OnReconnected(object sender, System.EventArgs e)
        {
            await RefreshConversationsAsync();
        }

        /// <summary>Public soft refresh (e.g. MainPage re-appears). Same as internal catch-up.</summary>
        public Task SoftRefreshAsync()
        {
            // Do not block navigation back to MainPage on the network round-trip.
            // Pushes and this background merge will update the existing rows in place.
            if (_detached) return Task.CompletedTask;
            _ = RefreshConversationsAsync();
            return Task.CompletedTask;
        }

        /// <summary>Single-flighted fetch+merge of the conversation list. Merges in place
        /// (updates existing rows' preview/unread/time, appends new rows) rather than
        /// clearing and rebuilding, so the bound ListView doesn't flicker/reset scroll
        /// position. Swallows failures -- this is always a best-effort catch-up refresh on
        /// top of an already-loaded list, never the primary load path.</summary>
        private async Task RefreshConversationsAsync()
        {
            if (_refreshing || _detached) return;
            _refreshing = true;
            try
            {
                IReadOnlyList<ChatConversation> fresh;
                try
                {
                    fresh = await _chat.GetConversationsAsync();
                }
                catch (System.Exception)
                {
                    return;
                }
                if (_detached) return;
                MergeConversations(fresh);
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>Merge a freshly-fetched conversation list into the bound collection:
        /// existing rows are updated in place (preview/unread/time/pin/mute stay as
        /// whatever the server says now), new rows are appended. Never clears the
        /// collection, so the ListView doesn't flicker or lose scroll position.</summary>
        private void MergeConversations(IReadOnlyList<ChatConversation> fresh)
        {
            if (fresh == null) return;
            var muteAll = NotificationMuteGate.IsMuteAll();
            foreach (var f in fresh)
            {
                // Keep UI + local gate aligned when global 全部消息免打扰 is on.
                if (muteAll && !string.IsNullOrEmpty(f.Id) && !NotificationMuteGate.IsSpecial(f.Id))
                {
                    f.IsMuted = true;
                    NotificationMuteGate.SetConversationMuted(f.Id, true);
                }
                else if (NotificationMuteGate.IsConversationMuted(f.Id))
                {
                    f.IsMuted = true;
                }

                var existing = Conversations.FirstOrDefault(c => c.Id == f.Id);
                if (existing != null)
                {
                    existing.Title = f.Title;
                    existing.Preview = f.Preview;
                    existing.LastTime = f.LastTime;
                    // Prefer the higher of server vs app-lifetime store (never clobber a
                    // live badge with a stale 0 from an older server snapshot race).
                    UnreadBadgeStore.SetAtLeast(f.Id, f.Unread);
                    existing.Unread = f.Unread;
                    UnreadBadgeStore.ApplyTo(existing);
                    existing.IsPinned = f.IsPinned;
                    existing.IsMuted = f.IsMuted;
                    if (!string.IsNullOrEmpty(f.Announcement)) existing.Announcement = f.Announcement;
                }
                else
                {
                    UnreadBadgeStore.SetAtLeast(f.Id, f.Unread);
                    UnreadBadgeStore.ApplyTo(f);
                    Conversations.Add(f);
                }
            }
            ResortConversations();
        }

        /// <summary>Short preview text for a message, falling back to a "[类型]" tag for
        /// content types that don't carry display text (image/voice/etc). Duplicates
        /// ConversationViewModel's private ContentSummary(ChatMessage) -- kept separate
        /// rather than extracted to a shared helper because ConversationViewModel is owned
        /// by another workstream; leader should consider consolidating the two later.</summary>
        private static string PreviewFor(ChatMessage m)
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

        /// <summary>Sort conversations pinned-first, then by most-recent activity. Re-orders the bound collection in place.</summary>
        public void ResortConversations()
        {
            var ordered = Conversations
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastTime)
                .ToList();

            bool changed = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!object.ReferenceEquals(Conversations[i], ordered[i]))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed) return;

            for (int i = 0; i < ordered.Count; i++)
            {
                int current = Conversations.IndexOf(ordered[i]);
                if (current != i) Conversations.Move(current, i);
            }
        }
    }
}
