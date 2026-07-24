using System.Collections.Generic;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// App-lifetime unread counters that keep ticking even when MainPage has Detach()'d
    /// (user is inside a chat, settings, etc.). RealServer also tracks unread; the list
    /// merges with <see cref="ApplyTo"/> so whichever is higher wins after a reload.
    /// </summary>
    public static class UnreadBadgeStore
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        private static bool _hooked;

        /// <summary>Subscribe once to the app-wide chat service.</summary>
        public static void EnsureHooked(IChatService chat)
        {
            if (chat == null || _hooked) return;
            _hooked = true;
            chat.MessageReceived += OnMessageReceived;
        }

        private static void OnMessageReceived(object sender, ChatMessage msg)
        {
            if (msg == null) return;
            if (msg.Direction == MessageDirection.Outgoing) return;
            if (string.IsNullOrEmpty(msg.ConversationId)) return;
            // User is looking at this chat or has enabled mute — do not badge.
            if (msg.ConversationId == App.ActiveConversationId) return;
            try
            {
                var muted = Windows.Storage.ApplicationData.Current.LocalSettings.Values["qqr.muted." + msg.ConversationId];
                if (muted is bool b && b) return;
            }
            catch { }
            Increment(msg.ConversationId);
        }

        public static void Increment(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            lock (Gate)
            {
                int n;
                Counts.TryGetValue(conversationId, out n);
                Counts[conversationId] = n + 1;
            }
            UpdateTileBadge();
        }

        public static void Clear(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            lock (Gate) { Counts[conversationId] = 0; }
            UpdateTileBadge();
        }

        public static int Get(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return 0;
            lock (Gate)
            {
                int n;
                return Counts.TryGetValue(conversationId, out n) ? n : 0;
            }
        }

        /// <summary>Merge store counts into a conversation row (take the higher unread).</summary>
        public static void ApplyTo(ChatConversation c)
        {
            if (c == null) return;
            var local = Get(c.Id);
            if (local > c.Unread) c.Unread = local;
        }

        /// <summary>After server says N, keep store in sync so a later ApplyTo doesn't inflate.</summary>
        public static void SetAtLeast(string conversationId, int unread)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            lock (Gate)
            {
                int n;
                Counts.TryGetValue(conversationId, out n);
                if (unread > n) Counts[conversationId] = unread;
            }
            UpdateTileBadge();
        }

        private static void UpdateTileBadge()
        {
            try
            {
                int total = 0;
                lock (Gate)
                {
                    foreach (var v in Counts.Values) total += v;
                }

                var badgeXml = Windows.UI.Notifications.BadgeUpdateManager.GetTemplateContent(Windows.UI.Notifications.BadgeTemplateType.BadgeNumber);
                var badgeElement = badgeXml.SelectSingleNode("/badge") as Windows.Data.Xml.Dom.XmlElement;
                if (badgeElement != null)
                {
                    badgeElement.SetAttribute("value", total.ToString());
                }
                var badge = new Windows.UI.Notifications.BadgeNotification(badgeXml);
                Windows.UI.Notifications.BadgeUpdateManager.CreateBadgeUpdaterForApplication().Update(badge);
            }
            catch { }
        }
    }
}
