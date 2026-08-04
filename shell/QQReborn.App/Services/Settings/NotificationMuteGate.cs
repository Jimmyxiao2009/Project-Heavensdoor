using System;
using Windows.Storage;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Single place for toast / in-app banner mute decisions.
    /// Per-conversation mute: <c>qqr.muted.{id}</c>.
    /// Global "全部消息免打扰": <c>qqr.settings.muteAll</c> (特别关心 can still break through).
    /// </summary>
    public static class NotificationMuteGate
    {
        public const string MuteAllKey = "qqr.settings.muteAll";
        public const string MutePrefix = "qqr.muted.";
        public const string SpecialPrefix = "qqr.special.";

        public static bool IsMuteAll()
        {
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[MuteAllKey];
                return raw is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        public static void SetMuteAll(bool value)
        {
            try { ApplicationData.Current.LocalSettings.Values[MuteAllKey] = value; }
            catch { }
        }

        public static bool IsSpecial(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return false;
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[SpecialPrefix + conversationId];
                return raw is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        public static void SetSpecial(string conversationId, bool special)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try { ApplicationData.Current.LocalSettings.Values[SpecialPrefix + conversationId] = special; }
            catch { }
        }

        public static bool IsConversationMuted(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return false;
            try
            {
                var gate = ApplicationData.Current.LocalSettings.Values[MutePrefix + conversationId];
                if (gate is bool b && b) return true;
                if (gate is string s && string.Equals(s, "True", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        public static void SetConversationMuted(string conversationId, bool muted)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try { ApplicationData.Current.LocalSettings.Values[MutePrefix + conversationId] = muted; }
            catch { }
        }

        /// <summary>
        /// Whether Windows toast / in-app banner should be suppressed.
        /// 特别关心 breaks through global mute-all; per-conversation mute still applies
        /// only when the conversation is not special.
        /// </summary>
        public static bool ShouldSuppressNotification(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return true;
            if (IsSpecial(conversationId)) return false;
            if (IsMuteAll()) return true;
            return IsConversationMuted(conversationId);
        }
    }
}
