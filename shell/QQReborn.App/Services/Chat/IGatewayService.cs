using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Extended gateway surface (RealServer / NapCat). Production: <see cref="RemoteChatService"/>.
    /// Mock chat does <b>not</b> implement this — UI should null-check <see cref="AppServices.Gateway"/>.
    /// Prefer this over casting to <see cref="RemoteChatService"/> so pages stay testable and mock-safe.
    /// </summary>
    public interface IGatewayService : IChatService
    {
        // ---- lifecycle ----
        void StartAutoConnect();
        Task ForceReconnectAsync();
        Task<bool> ConfigureAccountAsync(string expectedUin = null);

        // ---- pushes beyond IChatService ----
        event EventHandler<MessageRecalledInfo> MessageRecalled;
        event EventHandler<ConversationFlagsChangedInfo> ConversationFlagsChanged;
        event EventHandler<ConversationReadInfo> ConversationRead;
        event EventHandler<QrCodeInfo> QrCodeReceived;
        event EventHandler<LoginStatusInfo> LoginStatusChanged;
        event EventHandler SpaceFeedUpdated;
        event EventHandler Reconnected;
        event EventHandler SessionDataUpdated;

        // ---- account / profile ----
        Task MarkConversationReadAsync(string conversationId, string lastReadAt = null);
        Task<bool> MarkAllAsReadAsync();
        Task<UserProfile> GetUserProfileAsync(long uin);
        Task<bool> SetFriendRemarkAsync(long uin, string remark);
        Task<bool> DeleteFriendAsync(long uin, bool tempBlock = false, bool bothDel = false);
        Task<bool> SendLikeAsync(long targetUin, int count = 1);
        Task<long> GetProfileLikeCountAsync(long uin = 0);
        Task<string> GetUserStatusTextAsync(long uin);
        Task<bool> SetSelfProfileAsync(string nickname = null, string signature = null);
        Task<bool> SetOnlineStatusAsync(int status, int extStatus = 0, int batteryStatus = 0);
        Task<bool> SetAvatarAsync(string imageBase64);

        // ---- messages / media ----
        Task<ChatMessage> SendTextWithReplyAsync(string conversationId, string text, string replyToMessageId, string mentionsJson = null);
        Task<ChatMessage> SendFileAsync(string conversationId, byte[] fileBytes, string fileName);
        Task<ChatMessage> SendVideoAsync(string conversationId, string videoPath);
        Task<EarlierMessagesResult> GetEarlierMessagesAsync(string conversationId, string beforeMessageId, int count);
        Task<bool> RecallMessageAsync(string conversationId, string messageId);
        Task<bool> SendNudgeAsync(string conversationId, long targetUin);
        Task<string> GetMediaUrlAsync(string messageId);
        Task<VoicePlayableResult> GetVoicePlayableAsync(string messageId);
        Task<IReadOnlyList<ForwardEntry>> GetForwardDetailsAsync(string messageId);
        Task<string> GetFileDownloadUrlAsync(string conversationId, string fileId);
        Task<string> FetchPttTextAsync(string messageId);
        Task<bool> SetGroupReactionAsync(string conversationId, string messageId, string code, bool isAdd);
        Task<bool> SetEssenceAsync(string messageId, bool set = true);

        // ---- group admin ----
        Task<bool> QuitGroupAsync(string conversationId);
        Task<bool> GroupRenameAsync(string conversationId, string newName);
        Task<bool> GroupMemberRenameAsync(string conversationId, long targetUin, string newName);
        Task<bool> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title);
        Task<bool> SetGroupAdminAsync(string conversationId, long targetUin, bool enable);
        Task<bool> SetGroupBanAsync(string conversationId, long targetUin, int durationSeconds);
        Task<bool> SetGroupWholeBanAsync(string conversationId, bool enable);
        Task<bool> SetGroupKickAsync(string conversationId, long targetUin, bool rejectAddRequest = false);
        Task<IReadOnlyList<GroupNoticeItem>> GetGroupNoticesAsync(string conversationId);
        Task<bool> SendGroupNoticeAsync(string conversationId, string content);
        Task<bool> DeleteGroupNoticeAsync(string conversationId, string noticeId);
        Task<GroupFilesResult> GetGroupFilesAsync(string conversationId, string folderId = null);
        Task<string> GetGroupFileUrlAsync(string conversationId, string fileId, int busid = 0);
        Task<bool> CreateGroupFolderAsync(string conversationId, string name);
        Task<bool> DeleteGroupFileAsync(string conversationId, string fileId, int busid = 0);
        Task<IReadOnlyList<string>> GetEssenceSummariesAsync(string conversationId);
        Task<string> GetGroupHonorSummaryAsync(string conversationId);
        Task<IReadOnlyList<string>> GetGroupShutListAsync(string conversationId);
        Task<bool> GroupSignAsync(string conversationId);
        Task<bool> SetGroupPortraitAsync(string conversationId, string imageBase64);
        Task<bool> SetGroupRemarkAsync(string conversationId, string remark);
        Task<JsonArray> GetGroupNotificationsAsync();
        Task<bool> HandleGroupNotificationAsync(long groupUin, ulong sequence, string notifType, string operate, string message = "", bool isFiltered = false);
        Task<int> GetGroupAtAllRemainAsync(string conversationId);
        Task<string> GetVersionInfoSummaryAsync();

        // ---- space (also via IMomentsService for feed UI) ----
        bool SpaceFeedHasMore { get; }
        Task<IReadOnlyList<Moment>> GetSpaceFeedAsync(bool forceRefresh = false);
        Task<bool> GetEarlierSpaceFeedAsync();
        Task<bool> SetSpaceLikeAsync(string momentId, bool isLiked);
        Task<bool> SetSpaceCommentAsync(string momentId, string text);
    }
}
