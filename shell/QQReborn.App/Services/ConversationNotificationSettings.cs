using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Keeps the UI model, local notification gate, and bridge flag update in one transaction.
    /// The local gate is applied immediately so notifications are suppressed during a slow
    /// request, then restored if the bridge rejects the change.
    /// </summary>
    public static class ConversationNotificationSettings
    {
        public static async Task<bool> TrySetMutedAsync(IChatService chat, ChatConversation conversation, bool muted)
        {
            if (chat == null || conversation == null || string.IsNullOrEmpty(conversation.Id)) return false;

            var previousRowState = conversation.IsMuted;
            var previousGateState = NotificationMuteGate.IsConversationMuted(conversation.Id);
            var previousMuteAllState = NotificationMuteGate.IsMuteAll();

            conversation.IsMuted = muted;
            NotificationMuteGate.SetConversationMuted(conversation.Id, muted);
            if (!muted && previousMuteAllState)
                NotificationMuteGate.SetMuteAll(false);

            try
            {
                await chat.SetConversationFlagsAsync(conversation.Id, null, muted);
                if (muted) UnreadBadgeStore.Clear(conversation.Id);
                return true;
            }
            catch
            {
                conversation.IsMuted = previousRowState;
                NotificationMuteGate.SetConversationMuted(conversation.Id, previousGateState);
                NotificationMuteGate.SetMuteAll(previousMuteAllState);
                return false;
            }
        }
    }
}
