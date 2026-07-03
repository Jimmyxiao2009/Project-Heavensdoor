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

        Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId);

        Task<ChatMessage> SendTextAsync(string conversationId, string text);

        Task<ChatMessage> SendImageAsync(string conversationId, string imagePath);

        Task<ChatMessage> SendStickerAsync(string conversationId, string stickerPath);

        Task<ChatMessage> SendVoiceAsync(string conversationId, string audioPath, int seconds);

        Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb);

        Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string conversationId);

        Task<IReadOnlyList<FriendRequest>> GetFriendRequestsAsync();

        Task AcceptFriendRequestAsync(FriendRequest request);

        /// <summary>Raised when a new message arrives from the backend.</summary>
        event EventHandler<ChatMessage> MessageReceived;

        /// <summary>Raised when the peer's typing state changes.</summary>
        event EventHandler<TypingState> TypingChanged;
    }
}
