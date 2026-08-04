using System.Text.Json.Nodes;
using static QQReborn.RealServer.Wire.WireJson;

namespace QQReborn.RealServer.Wire;

/// <summary>
/// Domain handlers for Shell wire <c>type</c> values.
/// Add a case + <see cref="KnownTypes"/> entry; keep transport in Program/WireRpc.
/// Do not strip multi-shape JSON flags, background refresh try/catch, or account-bind side effects —
/// see docs/RESILIENCE.md.
/// </summary>
public static class WireDispatch
{
    public static async Task<(JsonNode? data, string? error, bool handled)> TryHandleAsync(
        string type, JsonObject req, ISessionBackend sessions, ClientConnection conn)
    {
        switch (type)
        {
            // ---- session / lists ----
            case "getSelf":
                return (sessions.GetSelf(), null, true);
            case "getConversations":
                return (sessions.GetConversations(), null, true);
            case "getContacts":
                return (await sessions.GetContactsAsync(), null, true);
            case "getMessages":
            {
                var localOnly = B(req, "localOnly") == true;
                return (await sessions.GetMessagesAsync(S(req, "conversationId") ?? "", allowCloudBackfill: !localOnly), null, true);
            }
            case "getGroupMembers":
                return (await sessions.GetGroupMembersAsync(S(req, "conversationId") ?? ""), null, true);
            case "getFriendRequests":
                return (sessions.GetFriendRequests(), null, true);
            case "acceptFriendRequest":
                return (sessions.AcceptFriendRequest((long)N(req, "uin")), null, true);
            case "rejectFriendRequest":
                return (await sessions.RejectFriendRequestAsync((long)N(req, "uin")), null, true);
            case "configureAccount":
            {
                var expectedUin = S(req, "expectedUin") ?? S(req, "signUin") ?? "";
                var (data, error) = await sessions.ConfigureAccountAsync(expectedUin);
                if (error == null && conn.Session != null)
                {
                    var resultUin = data?["uin"] is JsonValue jv && jv.TryGetValue<long>(out var lu) ? lu
                        : data?["uin"] is JsonValue jvd && jvd.TryGetValue<double>(out var du) ? (long)du
                        : 0L;
                    if (resultUin <= 0 && long.TryParse(expectedUin, out var reqUin))
                        resultUin = reqUin;
                    if (resultUin > 0) conn.Session.Uin = resultUin;
                }
                return (data, error, true);
            }
            case "setConversationFlags":
                return (sessions.SetConversationFlags(
                    S(req, "conversationId") ?? "", B(req, "isPinned"), B(req, "isMuted")), null, true);
            case "markConversationRead":
                return (sessions.MarkConversationRead(
                    S(req, "conversationId") ?? "", S(req, "lastReadAt")), null, true);
            case "markAllAsRead":
            {
                var (data, error) = await sessions.MarkAllAsReadAsync();
                return (data, error, true);
            }

            // ---- messaging ----
            case "getEarlierMessages":
            {
                var (data, error) = await sessions.GetEarlierMessagesAsync(
                    S(req, "conversationId") ?? "", S(req, "beforeId"), (int)N(req, "count"));
                return (data, error, true);
            }
            case "recallMessage":
                return (await sessions.RecallMessageAsync(S(req, "conversationId") ?? "", S(req, "messageId") ?? ""), null, true);
            case "nudge":
            {
                var (data, error) = await sessions.SendNudgeAsync(S(req, "conversationId") ?? "", (long)N(req, "targetUin"));
                return (data, error, true);
            }
            case "send":
            {
                var (data, error) = await sessions.SendAsync(
                    S(req, "conversationId") ?? "", S(req, "text") ?? "", S(req, "replyToId"),
                    S(req, "contentType") ?? "Text", S(req, "placeName"), S(req, "address"), S(req, "thumb"),
                    S(req, "imageBase64"), req["imagesBase64"], S(req, "audioBase64"), (int)N(req, "voiceSeconds"),
                    S(req, "fileBase64"), S(req, "fileName"), S(req, "mentions"), N(req, "latitude"), N(req, "longitude"));
                return (data, error, true);
            }
            case "forward":
            {
                var (data, error) = await sessions.ForwardAsync(
                    S(req, "conversationId") ?? "", S(req, "messageId") ?? "");
                return (data, error, true);
            }
            case "forwardMany":
            {
                var (data, error) = await sessions.ForwardManyAsync(
                    S(req, "conversationId") ?? "", req["messageIds"] as JsonArray ?? new JsonArray());
                return (data, error, true);
            }
            case "getMediaUrl":
            {
                var (data, error) = await sessions.GetMediaUrlAsync(S(req, "messageId") ?? "");
                return (data, error, true);
            }
            case "getVoicePlayable":
            {
                var (data, error) = await sessions.GetVoicePlayableAsync(S(req, "messageId") ?? "");
                return (data, error, true);
            }
            case "getFavoriteStickers":
            {
                var (data, error) = await sessions.GetFavoriteStickersAsync((int)N(req, "count"));
                return (data, error, true);
            }
            case "getForwardDetails":
            {
                var (data, error) = await sessions.GetForwardDetailsAsync(S(req, "messageId") ?? "");
                return (data, error, true);
            }
            case "getFileDownloadUrl":
            {
                var (data, error) = await sessions.GetFileDownloadUrlAsync(
                    S(req, "conversationId") ?? "", S(req, "fileId") ?? "");
                return (data, error, true);
            }
            case "fetchPttText":
            {
                var (data, error) = await sessions.FetchPttTextAsync(S(req, "messageId") ?? "");
                return (data, error, true);
            }
            case "setGroupReaction":
            {
                var (data, error) = await sessions.SetGroupReactionAsync(
                    S(req, "conversationId") ?? "", S(req, "messageId") ?? "",
                    S(req, "code") ?? "", Flag(req, "isAdd", true));
                return (data, error, true);
            }
            case "setEssence":
            {
                var (data, error) = await sessions.SetEssenceAsync(S(req, "messageId") ?? "", B(req, "set") != false);
                return (data, error, true);
            }

            // ---- friends / profile ----
            case "sendLike":
            {
                var (data, error) = await sessions.SendLikeAsync((long)N(req, "targetUin"), (int)N(req, "count"));
                return (data, error, true);
            }
            case "getUserProfile":
            {
                var (data, error) = await sessions.GetUserProfileAsync((long)N(req, "uin"));
                return (data, error, true);
            }
            case "setFriendRemark":
            {
                var (data, error) = await sessions.SetFriendRemarkAsync((long)N(req, "uin"), S(req, "remark") ?? "");
                return (data, error, true);
            }
            case "deleteFriend":
            {
                var (data, error) = await sessions.DeleteFriendAsync(
                    (long)N(req, "uin"), B(req, "tempBlock") == true, B(req, "bothDel") == true);
                return (data, error, true);
            }
            case "setSelfProfile":
            {
                var (data, error) = await sessions.SetSelfProfileAsync(S(req, "nickname"), S(req, "signature"));
                return (data, error, true);
            }
            case "setOnlineStatus":
            {
                var (data, error) = await sessions.SetOnlineStatusAsync(
                    (int)N(req, "status"), (int)N(req, "extStatus"), (int)N(req, "batteryStatus"));
                return (data, error, true);
            }
            case "setAvatar":
            {
                var (data, error) = await sessions.SetAvatarAsync(S(req, "imageBase64") ?? "");
                return (data, error, true);
            }
            case "getProfileLike":
            {
                var uin = (long)N(req, "uin");
                var (data, error) = await sessions.GetProfileLikeAsync(uin > 0 ? uin : null);
                return (data, error, true);
            }
            case "getUserStatus":
            {
                var (data, error) = await sessions.GetUserStatusAsync((long)N(req, "uin"));
                return (data, error, true);
            }
            case "getVersionInfo":
            {
                var (data, error) = await sessions.GetVersionInfoAsync();
                return (data, error, true);
            }

            // ---- group admin ----
            case "quitGroup":
                return (await sessions.QuitGroupAsync(S(req, "conversationId") ?? ""), null, true);
            case "groupRename":
            {
                var (data, error) = await sessions.GroupRenameAsync(S(req, "conversationId") ?? "", S(req, "name") ?? "");
                return (data, error, true);
            }
            case "groupMemberRename":
            {
                var (data, error) = await sessions.GroupMemberRenameAsync(
                    S(req, "conversationId") ?? "", (long)N(req, "targetUin"), S(req, "name") ?? "");
                return (data, error, true);
            }
            case "groupSetSpecialTitle":
            {
                var (data, error) = await sessions.GroupSetSpecialTitleAsync(
                    S(req, "conversationId") ?? "", (long)N(req, "targetUin"), S(req, "title") ?? "");
                return (data, error, true);
            }
            case "setGroupAdmin":
            {
                var (data, error) = await sessions.SetGroupAdminAsync(
                    S(req, "conversationId") ?? "", (long)N(req, "targetUin"), Flag(req, "enable"));
                return (data, error, true);
            }
            case "setGroupBan":
            {
                var (data, error) = await sessions.SetGroupBanAsync(
                    S(req, "conversationId") ?? "", (long)N(req, "targetUin"), (int)N(req, "duration"));
                return (data, error, true);
            }
            case "setGroupWholeBan":
            {
                var (data, error) = await sessions.SetGroupWholeBanAsync(
                    S(req, "conversationId") ?? "", Flag(req, "enable"));
                return (data, error, true);
            }
            case "setGroupKick":
            {
                var (data, error) = await sessions.SetGroupKickAsync(
                    S(req, "conversationId") ?? "", (long)N(req, "targetUin"), Flag(req, "rejectAddRequest"));
                return (data, error, true);
            }
            case "getGroupNotifications":
            {
                var (data, error) = await sessions.GetGroupNotificationsAsync();
                return (data, error, true);
            }
            case "handleGroupNotification":
            {
                var (data, error) = await sessions.HandleGroupNotificationAsync(
                    (long)N(req, "groupUin"), (ulong)N(req, "sequence"),
                    S(req, "notifType") ?? "join", S(req, "operate") ?? "accept",
                    S(req, "message"), B(req, "isFiltered") == true);
                return (data, error, true);
            }
            case "getGroupNotices":
            {
                var (data, error) = await sessions.GetGroupNoticesAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }
            case "sendGroupNotice":
            {
                var (data, error) = await sessions.SendGroupNoticeAsync(
                    S(req, "conversationId") ?? "", S(req, "content") ?? "");
                return (data, error, true);
            }
            case "deleteGroupNotice":
            {
                var (data, error) = await sessions.DeleteGroupNoticeAsync(
                    S(req, "conversationId") ?? "", S(req, "noticeId") ?? "");
                return (data, error, true);
            }
            case "getGroupFiles":
            {
                var (data, error) = await sessions.GetGroupFilesAsync(
                    S(req, "conversationId") ?? "", S(req, "folderId"));
                return (data, error, true);
            }
            case "getGroupFileUrl":
            {
                var (data, error) = await sessions.GetGroupFileUrlAsync(
                    S(req, "conversationId") ?? "", S(req, "fileId") ?? "", (int)N(req, "busid"));
                return (data, error, true);
            }
            case "getEssenceList":
            {
                var (data, error) = await sessions.GetEssenceListAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }
            case "getGroupHonor":
            {
                var (data, error) = await sessions.GetGroupHonorAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }
            case "getGroupShutList":
            {
                var (data, error) = await sessions.GetGroupShutListAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }
            case "groupSign":
            {
                var (data, error) = await sessions.GroupSignAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }
            case "setGroupPortrait":
            {
                var (data, error) = await sessions.SetGroupPortraitAsync(
                    S(req, "conversationId") ?? "", S(req, "imageBase64") ?? "");
                return (data, error, true);
            }
            case "setGroupRemark":
            {
                var (data, error) = await sessions.SetGroupRemarkAsync(
                    S(req, "conversationId") ?? "", S(req, "remark") ?? "");
                return (data, error, true);
            }
            case "createGroupFolder":
            {
                var (data, error) = await sessions.CreateGroupFolderAsync(
                    S(req, "conversationId") ?? "", S(req, "name") ?? "");
                return (data, error, true);
            }
            case "deleteGroupFile":
            {
                var (data, error) = await sessions.DeleteGroupFileAsync(
                    S(req, "conversationId") ?? "", S(req, "fileId") ?? "", (int)N(req, "busid"));
                return (data, error, true);
            }
            case "getGroupAtAllRemain":
            {
                var (data, error) = await sessions.GetGroupAtAllRemainAsync(S(req, "conversationId") ?? "");
                return (data, error, true);
            }

            // ---- space ----
            case "getMoments":
            case "getSpaceFeed":
            {
                var data = sessions.GetSpaceFeed();
                if (data?["moments"] is JsonArray ma && ma.Count == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await sessions.FetchQzoneFeedNativeAsync(); }
                        catch (Exception ex) { Console.WriteLine("[!] moments refresh: " + ex.Message); }
                    });
                }
                return (data, null, true);
            }
            case "fetchSpaceFeed":
            {
                try { await sessions.FetchQzoneFeedNativeAsync(); }
                catch (Exception ex) { Console.WriteLine("[!] fetchSpaceFeed: " + ex.Message); }
                return (sessions.GetSpaceFeed(), null, true);
            }
            case "fetchEarlierSpaceFeed":
                return (await sessions.FetchEarlierSpaceFeedAsync((int)N(req, "num")), null, true);
            case "setSpaceLike":
            {
                var isLiked = Flag(req, "isLiked");
                return (sessions.SetSpaceLike(S(req, "momentId") ?? "", isLiked), null, true);
            }
            case "setSpaceComment":
                return (sessions.SetSpaceComment(S(req, "momentId") ?? "", S(req, "text") ?? ""), null, true);

            default:
                return (null, null, false);
        }
    }

    /// <summary>Known wire types (for tests / docs). Keep in sync with switch above.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } = new[]
    {
        "getSelf", "getConversations", "getContacts", "getMessages", "getGroupMembers",
        "getFriendRequests", "acceptFriendRequest", "rejectFriendRequest", "configureAccount",
        "setConversationFlags", "markConversationRead", "markAllAsRead",
        "getEarlierMessages", "recallMessage", "nudge", "send", "forward", "forwardMany",
        "getMediaUrl", "getVoicePlayable", "getFavoriteStickers", "getForwardDetails",
        "getFileDownloadUrl", "fetchPttText", "setGroupReaction", "setEssence",
        "sendLike", "getUserProfile", "setFriendRemark", "deleteFriend", "setSelfProfile",
        "setOnlineStatus", "setAvatar", "getProfileLike", "getUserStatus", "getVersionInfo",
        "quitGroup", "groupRename", "groupMemberRename", "groupSetSpecialTitle",
        "setGroupAdmin", "setGroupBan", "setGroupWholeBan", "setGroupKick",
        "getGroupNotifications", "handleGroupNotification", "getGroupNotices",
        "sendGroupNotice", "deleteGroupNotice", "getGroupFiles", "getGroupFileUrl",
        "getEssenceList", "getGroupHonor", "getGroupShutList", "groupSign",
        "setGroupPortrait", "setGroupRemark", "createGroupFolder", "deleteGroupFile",
        "getGroupAtAllRemain",
        "getMoments", "getSpaceFeed", "fetchSpaceFeed", "fetchEarlierSpaceFeed",
        "setSpaceLike", "setSpaceComment",
    };
}
