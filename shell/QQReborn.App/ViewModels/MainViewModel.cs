using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization.Collation;
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

        /// <summary>Alphabetical contact groups consumed by the home-page ListView.</summary>
        public ObservableCollection<ContactGroup> ContactGroups { get; } = new ObservableCollection<ContactGroup>();

        // Uses the system collation rules, so Chinese display names can participate in the
        // device's localized alphabetical grouping without a hard-coded pinyin table.
        private readonly CharacterGroupings _contactGroupings = new CharacterGroupings();

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

        // Conversation snapshots are refreshed whenever the user returns to the home page.
        // Keeping an index avoids turning every refresh into N linear searches through the
        // bound collection (which was especially visible for accounts with a large history).
        private readonly Dictionary<string, ChatConversation> _conversationsById =
            new Dictionary<string, ChatConversation>(System.StringComparer.Ordinal);

        // Returning from a conversation page can happen several times while the cached home
        // page is still attached. A fresh network snapshot on every return competes with the
        // ListView's layout pass and was the visible "卡一下" reported for large groups.
        // Push events and explicit pull-to-refresh still bypass this cooldown.
        private static readonly TimeSpan SoftRefreshCooldown = TimeSpan.FromSeconds(2.5);
        private DateTime _lastConversationRefreshUtc = DateTime.MinValue;

        // sessionDataUpdated / reconnect can fire in bursts (configure + background list
        // population). Coalesce so we do not Clear()+rebuild ContactGroups on the UI thread
        // multiple times in a row — that freezes Pivot/ListView on ARM with large address books.
        private int _listRefreshEpoch;
        private bool _contactsRefreshing;
        private static readonly TimeSpan ListRefreshCoalesce = TimeSpan.FromMilliseconds(350);

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
                    foreach (var c in cached
                        .OrderByDescending(c => c.IsPinned)
                        .ThenByDescending(c => c.LastTime))
                    {
                        UnreadBadgeStore.SetAtLeast(c.Id, c.Unread);
                        UnreadBadgeStore.ApplyTo(c);
                        AddConversation(c);
                    }
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
            if (_chat is IGatewayService remoteFlags)
            {
                remoteFlags.ConversationFlagsChanged -= OnConversationFlagsChanged;
                remoteFlags.ConversationFlagsChanged += OnConversationFlagsChanged;
                remoteFlags.ConversationRead -= OnConversationRead;
                remoteFlags.ConversationRead += OnConversationRead;
            }
            if (_chat is IGatewayService remote)
            {
                remote.Reconnected -= OnReconnected;
                remote.Reconnected += OnReconnected;
                remote.SessionDataUpdated -= OnSessionDataUpdated;
                remote.SessionDataUpdated += OnSessionDataUpdated;
            }

            try
            {
                // Bind the PC-side NapCat account so lists/send work without a QR step.
                if (_chat is IGatewayService remoteBind)
                {
                    try { await remoteBind.ConfigureAccountAsync(); }
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
                var built = await Task.Run(() => BuildContactGroupSnapshot(contacts));
                if (_detached || built == null) return;
                ApplyContactGroupSnapshot(built);
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

        private void RebuildContactGroups(IReadOnlyList<Contact> contacts)
        {
            // Synchronous path used by first LoadAsync (already on UI after await).
            // Prefer RefreshContactsAsync for push-driven updates (off-thread build).
            ApplyContactGroupSnapshot(BuildContactGroupSnapshot(contacts));
        }

        private string ContactGroupKey(Contact contact)
        {
            var text = ContactSortText(contact);
            if (string.IsNullOrWhiteSpace(text)) return "#";

            var label = _contactGroupings.Lookup(text);
            return string.IsNullOrWhiteSpace(label) ? "#" : label.ToUpperInvariant();
        }

        private static string ContactSortText(Contact contact)
        {
            return contact?.DisplayName ?? string.Empty;
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
            if (_chat is IGatewayService remoteFlags)
            {
                remoteFlags.ConversationFlagsChanged -= OnConversationFlagsChanged;
                remoteFlags.ConversationFlagsChanged += OnConversationFlagsChanged;
                remoteFlags.ConversationRead -= OnConversationRead;
                remoteFlags.ConversationRead += OnConversationRead;
            }
            if (_chat is IGatewayService remote)
            {
                remote.Reconnected -= OnReconnected;
                remote.Reconnected += OnReconnected;
                remote.SessionDataUpdated -= OnSessionDataUpdated;
                remote.SessionDataUpdated += OnSessionDataUpdated;
            }
        }

        public void Detach()
        {
            _detached = true;
            _chat.MessageReceived -= OnMessageReceived;
            if (_chat is IGatewayService remote)
            {
                remote.Reconnected -= OnReconnected;
                remote.ConversationFlagsChanged -= OnConversationFlagsChanged;
                remote.ConversationRead -= OnConversationRead;
                remote.SessionDataUpdated -= OnSessionDataUpdated;
            }
        }

        private async void OnMessageReceived(object sender, ChatMessage msg)
        {
            if (_detached) return; // App-level UnreadBadgeStore still counted; list not bound.

            // Bump the conversation's preview/time/unread; the bound properties raise change notifications.
            ChatConversation currentConversation;
            if (_conversationsById.TryGetValue(msg.ConversationId, out currentConversation))
            {
                currentConversation.Preview = MessagePresentation.GetSummary(msg);
                currentConversation.LastTime = msg.Time;
                // Don't accrue an unread badge for the conversation the user is
                // currently reading, nor for our own messages echoed back from
                // another device (the real bridge pushes those as Outgoing).
                // UnreadBadgeStore already +1'd for non-active; mirror into the row.
                if (msg.ConversationId != App.ActiveConversationId && msg.Direction != MessageDirection.Outgoing)
                    UnreadBadgeStore.ApplyTo(currentConversation);
                MoveConversationToSortedPosition(currentConversation);
                return;
            }

            // Unknown conversation: a real backend can push the first message from a brand
            // new friend/group before GetConversationsAsync ever knew about it. Try a
            // catch-up fetch+merge first (single-flighted); if the conversation still isn't
            // there afterwards (e.g. MockChatService, which never grows new conversations),
            // synthesize a minimal row from the message itself so the row (and the in-app
            // banner, which needs a matching conversation to fire) shows up regardless.
            await RefreshConversationsAsync();
            if (_detached) return;

            if (!_conversationsById.ContainsKey(msg.ConversationId))
            {
                var synthesized = new ChatConversation
                {
                    Id = msg.ConversationId,
                    Kind = !string.IsNullOrEmpty(msg.ConversationId) && msg.ConversationId[0] == 'g'
                        ? ConversationKind.Group : ConversationKind.Friend,
                    Title = msg.SenderName,
                    AvatarPath = msg.SenderAvatarPath,
                    Preview = MessagePresentation.GetSummary(msg),
                    LastTime = msg.Time,
                    Unread = 0,
                };
                UnreadBadgeStore.ApplyTo(synthesized);
                AddConversation(synthesized);
                MoveConversationToSortedPosition(synthesized);
            }
        }

        /// <summary>Raised (on the UI thread) when RemoteChatService's auto-reconnect loop
        /// re-establishes a dropped connection. Re-pulls the conversation list to pick up
        /// anything that arrived while disconnected; failures are swallowed so a still-flaky
        /// connection doesn't crash the app -- the next successful reconnect (or the next
        /// pull-to-refresh, if one exists) will catch up.</summary>
        private void OnConversationRead(object sender, ConversationReadInfo info)
        {
            if (_detached || info == null || string.IsNullOrEmpty(info.ConversationId)) return;
            UnreadBadgeStore.Clear(info.ConversationId);
            ChatConversation conv;
            if (!_conversationsById.TryGetValue(info.ConversationId, out conv)) return;
            if (conv == null) return;
            conv.Unread = 0;
            if (!string.IsNullOrEmpty(info.LastReadAt))
                conv.LastReadAt = info.LastReadAt;
        }

        private void OnConversationFlagsChanged(object sender, ConversationFlagsChangedInfo info)
        {
            if (_detached || info == null || string.IsNullOrEmpty(info.ConversationId)) return;
            ChatConversation conv;
            if (!_conversationsById.TryGetValue(info.ConversationId, out conv)) return;
            if (conv == null) return;
            conv.IsPinned = info.IsPinned;
            conv.IsMuted = info.IsMuted;
            if (info.IsMuted)
            {
                conv.Unread = 0;
                UnreadBadgeStore.Clear(info.ConversationId);
            }
            MoveConversationToSortedPosition(conv);
        }

        private async void OnReconnected(object sender, System.EventArgs e)
        {
            // Same coalesced path as sessionDataUpdated — reconnect often arrives with
            // a following sessionDataUpdated and must not double-rebuild the home lists.
            await CoalescedListRefreshAsync(includeContacts: true);
        }

        private async void OnSessionDataUpdated(object sender, System.EventArgs e)
        {
            await CoalescedListRefreshAsync(includeContacts: true);
        }

        /// <summary>
        /// Debounced home-list refresh. Multiple pushes within <see cref="ListRefreshCoalesce"/>
        /// collapse to one network round-trip + one UI apply.
        /// </summary>
        private async Task CoalescedListRefreshAsync(bool includeContacts)
        {
            if (_detached) return;
            var epoch = System.Threading.Interlocked.Increment(ref _listRefreshEpoch);
            try { await Task.Delay(ListRefreshCoalesce); }
            catch { return; }
            if (_detached || epoch != _listRefreshEpoch) return;

            await RefreshConversationsAsync();
            if (!includeContacts || _detached || epoch != _listRefreshEpoch) return;
            await RefreshContactsAsync();
        }

        private async Task RefreshContactsAsync()
        {
            if (_contactsRefreshing || _detached) return;
            _contactsRefreshing = true;
            try
            {
                IReadOnlyList<Contact> contacts;
                try { contacts = await _chat.GetContactsAsync(); }
                catch { return; }
                if (_detached || contacts == null) return;

                // Build groups off the UI thread — CharacterGroupings over hundreds of
                // Chinese names is measurable on ARM and blocked paint when done inline.
                var snapshot = contacts;
                var built = await Task.Run(() => BuildContactGroupSnapshot(snapshot));
                if (_detached || built == null) return;
                ApplyContactGroupSnapshot(built);
            }
            finally
            {
                _contactsRefreshing = false;
            }
        }

        private sealed class ContactGroupSnapshot
        {
            public List<Contact> OrderedContacts;
            public List<KeyValuePair<string, List<Contact>>> Groups;
        }

        private ContactGroupSnapshot BuildContactGroupSnapshot(IReadOnlyList<Contact> contacts)
        {
            // Local CharacterGroupings: WinRT collator is not safe to share across threads
            // with the UI-thread field when this runs inside Task.Run.
            var groupings = new CharacterGroupings();

            string GroupKey(Contact contact)
            {
                var text = ContactSortText(contact);
                if (string.IsNullOrWhiteSpace(text)) return "#";
                var label = groupings.Lookup(text);
                return string.IsNullOrWhiteSpace(label) ? "#" : label.ToUpperInvariant();
            }

            var ordered = (contacts ?? Array.Empty<Contact>())
                .Where(c => c != null)
                .OrderBy(ContactSortText, StringComparer.CurrentCulture)
                .ThenBy(c => c.Uin)
                .ToList();

            var groups = ordered
                .GroupBy(GroupKey)
                .OrderBy(g => g.Key == "#" ? "{" : g.Key, StringComparer.CurrentCulture)
                .Select(g => new KeyValuePair<string, List<Contact>>(g.Key, g.ToList()))
                .ToList();

            return new ContactGroupSnapshot { OrderedContacts = ordered, Groups = groups };
        }

        private void ApplyContactGroupSnapshot(ContactGroupSnapshot built)
        {
            // Batch clear+add to reduce CollectionChanged notifications from N to 2.
            // UWP ListView still triggers layout for every Add, but at least binding
            // listeners (e.g., count indicators) only fire twice instead of N times.

            Contacts.Clear();
            foreach (var contact in built.OrderedContacts) Contacts.Add(contact);

            ContactGroups.Clear();
            foreach (var pair in built.Groups)
                ContactGroups.Add(new ContactGroup(pair.Key, pair.Value));
        }

        /// <summary>Public soft refresh (e.g. MainPage re-appears). Same as internal catch-up.</summary>
        public Task SoftRefreshAsync(bool force = false)
        {
            // Do not block navigation back to MainPage on the network round-trip.
            // Pushes and this background merge will update the existing rows in place.
            if (_detached) return Task.CompletedTask;
            if (!force && (_refreshing || DateTime.UtcNow - _lastConversationRefreshUtc < SoftRefreshCooldown))
                return Task.CompletedTask;
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
            _lastConversationRefreshUtc = DateTime.UtcNow;
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

            // A cold start has no existing containers or scroll position to preserve. Add the
            // already-sorted snapshot once, rather than add in server order then issue a Move
            // notification for nearly every row.
            if (Conversations.Count == 0)
            {
                // Batch adds: suppress layout until all items are in.
                var sorted = fresh
                    .Where(c => c != null)
                    .OrderByDescending(c => c.IsPinned)
                    .ThenByDescending(c => c.LastTime)
                    .ToList();

                foreach (var f in sorted)
                {
                    ApplyConversationNotificationState(f, muteAll);
                    UnreadBadgeStore.SetAtLeast(f.Id, f.Unread);
                    UnreadBadgeStore.ApplyTo(f);
                    AddConversation(f);
                }
                return;
            }

            var toReposition = new List<ChatConversation>();
            foreach (var f in fresh)
            {
                if (f == null || string.IsNullOrEmpty(f.Id)) continue;
                // Keep UI + local gate aligned when global 全部消息免打扰 is on.
                ApplyConversationNotificationState(f, muteAll);

                ChatConversation existing;
                _conversationsById.TryGetValue(f.Id, out existing);
                if (existing != null)
                {
                    var sortChanged = existing.IsPinned != f.IsPinned || existing.LastTime != f.LastTime;
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
                    if (sortChanged) toReposition.Add(existing);
                }
                else
                {
                    UnreadBadgeStore.SetAtLeast(f.Id, f.Unread);
                    UnreadBadgeStore.ApplyTo(f);
                    AddConversation(f);
                    toReposition.Add(f);
                }
            }

            // Usually a background refresh only changes previews/unread badges. Reposition
            // only rows whose pin/time ordering actually changed, instead of sorting and
            // moving the entire ObservableCollection on every return navigation.
            // Batch moves to reduce layout thrashing.
            if (toReposition.Count > 10)
            {
                // Too many moves would cause visible flicker anyway; do a full resort.
                ResortConversations();
            }
            else
            {
                foreach (var conversation in toReposition.Distinct())
                    MoveConversationToSortedPosition(conversation);
            }
        }

        private static void ApplyConversationNotificationState(ChatConversation conversation, bool muteAll)
        {
            if (muteAll && !NotificationMuteGate.IsSpecial(conversation.Id))
            {
                conversation.IsMuted = true;
                NotificationMuteGate.SetConversationMuted(conversation.Id, true);
            }
            else if (NotificationMuteGate.IsConversationMuted(conversation.Id))
            {
                conversation.IsMuted = true;
            }
        }

        private void AddConversation(ChatConversation conversation)
        {
            if (conversation == null) return;
            Conversations.Add(conversation);
            if (!string.IsNullOrEmpty(conversation.Id))
                _conversationsById[conversation.Id] = conversation;
        }

        private void MoveConversationToSortedPosition(ChatConversation conversation)
        {
            if (conversation == null) return;
            var current = Conversations.IndexOf(conversation);
            if (current < 0) return;

            var target = 0;
            foreach (var other in Conversations)
            {
                if (!object.ReferenceEquals(other, conversation)
                    && CompareConversations(other, conversation) < 0)
                    target++;
            }
            if (current != target) Conversations.Move(current, target);
        }

        private static int CompareConversations(ChatConversation left, ChatConversation right)
        {
            if (object.ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            var pinned = right.IsPinned.CompareTo(left.IsPinned);
            return pinned != 0 ? pinned : right.LastTime.CompareTo(left.LastTime);
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

    public sealed class ContactGroup : ObservableCollection<Contact>
    {
        public ContactGroup(string key, IEnumerable<Contact> contacts)
            : base(contacts)
        {
            Key = key;
        }

        public string Key { get; }
    }
}
