using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
        public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string conversationId)
        {
            var arr = JsonArray.Parse(await RequestAsync("getGroupMembers",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<GroupMember>();
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new GroupMember
                {
                    Uin = (long)o.GetNamedNumber("uin", 0),
                    Name = Str(o, "name"),
                    AvatarPath = Str(o, "avatarPath"),
                    Role = Str(o, "role"),
                });
            }
            return list;
        }


        public async Task<bool> QuitGroupAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("quitGroup",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            return data.GetNamedBoolean("left", false) || data.GetNamedBoolean("ok", false);
        }

        /// <summary>Sends a "poke"/nudge to the given target within a conversation. Returns
        /// data.sent as reported by the server (same honesty convention as AcceptFriendRequestAsync's
        /// handled flag / QuitGroupAsync's left flag).</summary>

        public async Task<bool> GroupRenameAsync(string conversationId, string newName)
        {
            var data = JsonObject.Parse(await RequestAsync("groupRename", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["name"] = JsonValue.CreateStringValue(newName);
            }));
            return data.GetNamedBoolean("renamed", false);
        }


        public async Task<bool> GroupMemberRenameAsync(string conversationId, long targetUin, string newName)
        {
            var data = JsonObject.Parse(await RequestAsync("groupMemberRename", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["name"] = JsonValue.CreateStringValue(newName);
            }));
            return data.GetNamedBoolean("renamed", false);
        }


        public async Task<bool> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title)
        {
            var data = JsonObject.Parse(await RequestAsync("groupSetSpecialTitle", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["title"] = JsonValue.CreateStringValue(title);
            }));
            return data.GetNamedBoolean("set", false);
        }

        /// <summary>设置群管理员</summary>

        public async Task<bool> SetGroupAdminAsync(string conversationId, long targetUin, bool enable)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupAdmin", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["enable"] = JsonValue.CreateBooleanValue(enable);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>群组单人禁言</summary>

        public async Task<bool> SetGroupBanAsync(string conversationId, long targetUin, int durationSeconds)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupBan", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["duration"] = JsonValue.CreateNumberValue(durationSeconds);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>群组全员禁言</summary>

        public async Task<bool> SetGroupWholeBanAsync(string conversationId, bool enable)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupWholeBan", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["enable"] = JsonValue.CreateBooleanValue(enable);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>踢出群成员</summary>

        public async Task<bool> SetGroupKickAsync(string conversationId, long targetUin, bool rejectAddRequest = false)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupKick", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["rejectAddRequest"] = JsonValue.CreateBooleanValue(rejectAddRequest);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>设置好友备注 — NapCat set_friend_remark</summary>

        public async Task<IReadOnlyList<GroupNoticeItem>> GetGroupNoticesAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupNotices",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<GroupNoticeItem>();
            var arr = data?.GetNamedArray("notices");
            if (arr == null) return list;
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new GroupNoticeItem
                {
                    Id = Str(o, "id"),
                    Content = Str(o, "content"),
                    Time = (long)o.GetNamedNumber("time", 0),
                });
            }
            return list;
        }

        /// <summary>发送群公告 — NapCat _send_group_notice</summary>

        public async Task<bool> SendGroupNoticeAsync(string conversationId, string content)
        {
            var data = JsonObject.Parse(await RequestAsync("sendGroupNotice", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["content"] = JsonValue.CreateStringValue(content ?? "");
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>删除群公告 — NapCat _del_group_notice</summary>

        public async Task<bool> DeleteGroupNoticeAsync(string conversationId, string noticeId)
        {
            var data = JsonObject.Parse(await RequestAsync("deleteGroupNotice", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["noticeId"] = JsonValue.CreateStringValue(noticeId ?? "");
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>群文件列表 — get_group_root_files / get_group_files_by_folder</summary>

        public async Task<GroupFilesResult> GetGroupFilesAsync(string conversationId, string folderId = null)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupFiles", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                if (!string.IsNullOrEmpty(folderId))
                    r["folderId"] = JsonValue.CreateStringValue(folderId);
            }));
            var result = new GroupFilesResult();
            if (data == null) return result;
            var folders = data.GetNamedArray("folders");
            if (folders != null)
            {
                foreach (var n in folders)
                {
                    var o = n.GetObject();
                    result.Folders.Add(new GroupFileEntry
                    {
                        IsFolder = true,
                        FolderId = Str(o, "folderId"),
                        Name = Str(o, "name"),
                    });
                }
            }
            var files = data.GetNamedArray("files");
            if (files != null)
            {
                foreach (var n in files)
                {
                    var o = n.GetObject();
                    result.Files.Add(new GroupFileEntry
                    {
                        IsFolder = false,
                        FileId = Str(o, "fileId"),
                        Name = Str(o, "name"),
                        Size = (long)o.GetNamedNumber("size", 0),
                        Busid = (int)o.GetNamedNumber("busid", 0),
                        Uploader = Str(o, "uploader"),
                    });
                }
            }
            return result;
        }

        /// <summary>获取群文件下载链接 — get_group_file_url</summary>

        public async Task<string> GetGroupFileUrlAsync(string conversationId, string fileId, int busid = 0)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupFileUrl", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["fileId"] = JsonValue.CreateStringValue(fileId ?? "");
                r["busid"] = JsonValue.CreateNumberValue(busid);
            }));
            return data != null ? Str(data, "url") : null;
        }


        public async Task<string> GetGroupHonorSummaryAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupHonor",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            if (data == null) return "";
            // Keep raw JSON truncated for display — structure varies by NapCat version.
            var honor = data.GetNamedValue("honor");
            return honor != null ? honor.Stringify() : "";
        }


        public async Task<IReadOnlyList<string>> GetGroupShutListAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupShutList",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<string>();
            var arr = data?.GetNamedArray("members");
            if (arr == null) return list;
            foreach (var n in arr)
            {
                var o = n.GetObject();
                var name = Str(o, "name");
                var uin = (long)o.GetNamedNumber("uin", 0);
                list.Add(string.IsNullOrEmpty(name) ? uin.ToString() : name + " (" + uin + ")");
            }
            return list;
        }


        public async Task<bool> GroupSignAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("groupSign",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<bool> SetGroupPortraitAsync(string conversationId, string imageBase64)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupPortrait", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["imageBase64"] = JsonValue.CreateStringValue(imageBase64 ?? "");
            }, timeoutSeconds: 60));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<bool> SetGroupRemarkAsync(string conversationId, string remark)
        {
            var data = JsonObject.Parse(await RequestAsync("setGroupRemark", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["remark"] = JsonValue.CreateStringValue(remark ?? "");
            }));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<bool> CreateGroupFolderAsync(string conversationId, string name)
        {
            var data = JsonObject.Parse(await RequestAsync("createGroupFolder", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["name"] = JsonValue.CreateStringValue(name ?? "");
            }));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<bool> DeleteGroupFileAsync(string conversationId, string fileId, int busid = 0)
        {
            var data = JsonObject.Parse(await RequestAsync("deleteGroupFile", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["fileId"] = JsonValue.CreateStringValue(fileId ?? "");
                r["busid"] = JsonValue.CreateNumberValue(busid);
            }));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<int> GetGroupAtAllRemainAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("getGroupAtAllRemain",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            if (data == null) return -1;
            return (int)data.GetNamedNumber("remain", -1);
        }


        public async Task<JsonArray> GetGroupNotificationsAsync()
        {
            var raw = await RequestAsync("getGroupNotifications", null);
            if (string.IsNullOrEmpty(raw) || raw == "null") return new JsonArray();
            var data = JsonObject.Parse(raw);
            return data?.GetNamedArray("notifications", new JsonArray()) ?? new JsonArray();
        }


        public async Task<bool> HandleGroupNotificationAsync(long groupUin, ulong sequence, string notifType, string operate, string message = "", bool isFiltered = false)
        {
            var raw = await RequestAsync("handleGroupNotification", r =>
            {
                r["groupUin"] = JsonValue.CreateNumberValue(groupUin);
                r["sequence"] = JsonValue.CreateNumberValue(sequence);
                r["notifType"] = JsonValue.CreateStringValue(notifType);
                r["operate"] = JsonValue.CreateStringValue(operate);
                r["message"] = JsonValue.CreateStringValue(message ?? "");
                r["isFiltered"] = JsonValue.CreateBooleanValue(isFiltered);
            });
            if (string.IsNullOrEmpty(raw) || raw == "null") return false;
            var data = JsonObject.Parse(raw);
            return data?.GetNamedBoolean("ok", false) == true;
        }

        /// <summary>Group message reaction (emoji). isAdd=false removes.</summary>

        public async Task<bool> SetGroupReactionAsync(string conversationId, string messageId, string code, bool isAdd)
        {
            var raw = await RequestAsync("setGroupReaction", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId ?? "");
                r["messageId"] = JsonValue.CreateStringValue(messageId ?? "");
                r["code"] = JsonValue.CreateStringValue(code ?? "");
                r["isAdd"] = JsonValue.CreateBooleanValue(isAdd);
            });
            if (string.IsNullOrEmpty(raw) || raw == "null") return false;
            var data = JsonObject.Parse(raw);
            return data?.GetNamedBoolean("ok", false) == true;
        }

        /// <summary>Space / 动态 feed from webhook-ingested posts on RealServer.</summary>
    }
}
