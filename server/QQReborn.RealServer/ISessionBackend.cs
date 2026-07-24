using System.Text.Json.Nodes;

namespace QQReborn.RealServer;

/// <summary>
/// Protocol-agnostic session surface used by the WS host (<see cref="Program"/>).
/// Production implementation: <see cref="NapCat.NapCatSessionManager"/> (OneBot 11 / NapCat).
/// </summary>
public interface ISessionBackend
{
    /// <summary>Backend id for diagnostics (always "napcat" in product builds).</summary>
    string BackendId { get; }

    event Action<string>? Broadcast;

    Task<(JsonObject? data, string? error)> ConfigureAccountAsync(string signUrl, string? signToken, string signUinRaw);

    JsonObject GetSelf();
    JsonArray GetConversations();
    Task<JsonArray> GetContactsAsync();
    Task<JsonArray> GetMessagesAsync(string conversationId, bool allowCloudBackfill = true);
    Task<JsonArray> GetGroupMembersAsync(string conversationId);
    JsonArray GetFriendRequests();
    JsonObject AcceptFriendRequest(long uin);
    Task<(JsonObject? data, string? error)> GetUserProfileAsync(long uin);
    Task<(JsonObject? data, string? error)> GetEarlierMessagesAsync(string conversationId, string? beforeId, int count);
    Task<JsonObject> RecallMessageAsync(string conversationId, string messageId);
    Task<JsonObject> QuitGroupAsync(string conversationId);
    Task<(JsonObject? data, string? error)> SendNudgeAsync(string conversationId, long targetUin);
    Task<(JsonObject? data, string? error)> SetAvatarAsync(string imageBase64);
    Task<(JsonObject? data, string? error)> GetMediaUrlAsync(string messageId);
    Task<(JsonObject? data, string? error)> GetVoicePlayableAsync(string messageId);
    Task<(JsonObject? data, string? error)> GetFileDownloadUrlAsync(string conversationId, string fileId);
    Task<(JsonObject? data, string? error)> GetGroupNotificationsAsync();
    Task<(JsonObject? data, string? error)> HandleGroupNotificationAsync(
        long groupUin, ulong sequence, string notifType, string operate, string? message, bool isFiltered);
    Task<(JsonObject? data, string? error)> SetGroupReactionAsync(
        string conversationId, string messageId, string code, bool isAdd);

    JsonObject GetSpaceFeed();
    JsonObject SetSpaceLike(string momentId, bool isLiked);
    JsonObject IngestSpaceWebhook(JsonNode? body);
    Task FetchQzoneFeedNativeAsync();
    Task<JsonObject> FetchEarlierSpaceFeedAsync(int num = 20);

    Task<(JsonObject? data, string? error)> SendAsync(
        string conversationId, string text, string? replyToId = null,
        string contentType = "Text", string? placeName = null, string? address = null, string? thumb = null,
        string? imageBase64 = null, JsonNode? imagesBase64Node = null, string? audioBase64 = null, int voiceSeconds = 0,
        string? fileBase64 = null, string? fileName = null, string? mentionsJson = null);

    Task<(JsonObject? data, string? error)> ForwardAsync(string conversationId, string messageId);
    JsonObject SetConversationFlags(string conversationId, bool? isPinned, bool? isMuted);
    JsonObject MarkConversationRead(string conversationId);
    Task<(JsonObject? data, string? error)> GroupRenameAsync(string conversationId, string newName);
    Task<(JsonObject? data, string? error)> GroupMemberRenameAsync(string conversationId, long targetUin, string newName);
    Task<(JsonObject? data, string? error)> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title);
}
