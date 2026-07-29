using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public class TypingState
    {
        public string ConversationId { get; set; }
        public bool IsTyping { get; set; }
    }

    /// <summary>
    /// Abstraction over the QQ backend. The mock implementation drives the UI today;
    /// a LagrangeV2-based implementation will replace it later without UI changes.
    /// </summary>
    public interface IChatService
    {
        Task<SelfProfile> GetSelfAsync();

        Task<IReadOnlyList<ChatConversation>> GetConversationsAsync();

        Task<IReadOnlyList<Contact>> GetContactsAsync();

        /// <param name="localOnly">When true, do not trigger cloud history backfill (used by
        /// global search so it only scans the session cache and doesn't stampede the server).</param>
        Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, bool localOnly = false);

        Task<ChatMessage> SendTextAsync(string conversationId, string text, string mentionsJson = null);

        Task<ChatMessage> SendImageAsync(string conversationId, string imagePath);

        /// <summary>
        /// Send one protocol message that may combine caption text + one or more images
        /// (图文混排 / 多图). Empty text is allowed when at least one image is present.
        /// </summary>
        Task<ChatMessage> SendMixedAsync(string conversationId, string text, IReadOnlyList<string> imagePaths, string replyToMessageId = null, string mentionsJson = null);

        Task<ChatMessage> SendStickerAsync(string conversationId, string stickerPath);

        Task<ChatMessage> SendVoiceAsync(string conversationId, string audioPath, int seconds);

        Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb,
            double latitude = 0, double longitude = 0);

        Task<IReadOnlyList<string>> GetFavoriteStickersAsync();

        Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string conversationId);

        Task<IReadOnlyList<FriendRequest>> GetFriendRequestsAsync();

        Task<ChatMessage> ForwardMessageAsync(string targetConversationId, string messageId);

        Task<ChatMessage> ForwardMessagesAsync(string targetConversationId, IReadOnlyList<string> messageIds);

        Task AcceptFriendRequestAsync(FriendRequest request);

        /// <summary>
        /// Set pin (置顶) and/or mute (消息免打扰) for a conversation. Pass null for a
        /// flag to leave it unchanged. Completes after the bridge accepts the update.
        /// Local to the bridge (Lagrange has no public Tencent sync for these).
        /// </summary>
        Task SetConversationFlagsAsync(string conversationId, bool? isPinned, bool? isMuted);

        /// <summary>Raised when a new message arrives from the backend.</summary>
        event EventHandler<ChatMessage> MessageReceived;

        /// <summary>Raised when the peer's typing state changes.</summary>
        event EventHandler<TypingState> TypingChanged;
    }
}
