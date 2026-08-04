using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

public sealed partial class NapCatSessionManager
{

    public JsonObject SetConversationFlags(string conversationId, bool? isPinned, bool? isMuted)
    {
        if (string.IsNullOrEmpty(conversationId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-conversation" };
        if (isPinned == null && isMuted == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "no-flags" };

        JsonObject result;
        lock (_gate)
        {
            if (!_convPrefs.TryGetValue(conversationId, out var prev) || prev == null)
                prev = new ConvPref();
            var pinned = isPinned ?? prev.Pinned;
            var muted = isMuted ?? prev.Muted;
            prev.Pinned = pinned;
            prev.Muted = muted;
            if (muted)
                // Clearing only the row leaves the persisted pref.Unread intact;
                // the next list refresh then resurrects the old badge via ApplyPrefsTo.
                prev.Unread = 0;
            _convPrefs[conversationId] = prev;
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == conversationId);
            if (conv != null)
            {
                conv["isPinned"] = pinned;
                conv["isMuted"] = muted;
                if (muted) conv["unread"] = 0;
            }
            SavePrefs();
            result = new JsonObject
            {
                ["ok"] = true,
                ["conversationId"] = conversationId,
                ["isPinned"] = pinned,
                ["isMuted"] = muted,
            };
        }

        // Push to every connected Shell so pin/mute stays in sync across devices
        // that share this gateway (local authority). Official NTQQ cloud write is
        // not available via current NapCat APIs.
        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "conversationFlagsChanged",
            ["data"] = new JsonObject
            {
                ["conversationId"] = conversationId,
                ["isPinned"] = result["isPinned"]!.GetValue<bool>(),
                ["isMuted"] = result["isMuted"]!.GetValue<bool>(),
            },
        }.ToJsonString());

        return result;
    }

    public JsonObject MarkConversationRead(string conversationId, string? lastReadAt = null)
    {
        string readAt;
        if (!string.IsNullOrWhiteSpace(lastReadAt)
            && DateTimeOffset.TryParse(lastReadAt, out var parsed))
            readAt = parsed.ToUniversalTime().ToString("o");
        else
            readAt = DateTimeOffset.UtcNow.ToString("o");

        JsonObject result;
        lock (_gate)
        {
            if (!_convPrefs.TryGetValue(conversationId, out var pref) || pref == null)
                pref = new ConvPref();
            if (!string.IsNullOrEmpty(pref.LastReadAt)
                && DateTimeOffset.TryParse(pref.LastReadAt, out var prevAt)
                && DateTimeOffset.TryParse(readAt, out var nextAt)
                && nextAt < prevAt)
            {
                readAt = pref.LastReadAt;
            }
            pref.LastReadAt = readAt;
            pref.Unread = 0;
            _convPrefs[conversationId] = pref;

            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == conversationId);
            if (conv != null)
            {
                conv["unread"] = 0;
                conv["lastReadAt"] = readAt;
            }
            SavePrefs();
            result = new JsonObject
            {
                ["ok"] = true,
                ["conversationId"] = conversationId,
                ["lastReadAt"] = readAt,
                ["unread"] = 0,
            };
        }

        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "conversationRead",
            ["data"] = new JsonObject
            {
                ["conversationId"] = conversationId,
                ["lastReadAt"] = readAt,
                ["unread"] = 0,
            },
        }.ToJsonString());

        if (TryParseConv(conversationId, out var kind, out var peer))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (kind == 'g')
                        await _api.CallAsync("mark_group_msg_as_read", new JsonObject { ["group_id"] = peer });
                    else
                        await _api.CallAsync("mark_msg_as_read", new JsonObject { ["user_id"] = peer });
                }
                catch (Exception ex) { Console.WriteLine("[NapCat] mark read: " + ex.Message); }
            });
        }
        return result;
    }

    public async Task<(JsonObject? data, string? error)> GroupRenameAsync(string conversationId, string newName)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_name", new JsonObject { ["group_id"] = peer, ["group_name"] = newName });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> GroupMemberRenameAsync(string conversationId, long targetUin, string newName)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_card", new JsonObject
        {
            ["group_id"] = peer,
            ["user_id"] = targetUin,
            ["card"] = newName,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_special_title", new JsonObject
        {
            ["group_id"] = peer,
            ["user_id"] = targetUin,
            ["special_title"] = title,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SendLikeAsync(long targetUin, int count)
    {
        if (count < 1) count = 1;
        if (count > 10) count = 10; // QQ daily limit
        var (_, err) = await _api.CallAsync("send_like", new JsonObject
        {
            ["user_id"] = targetUin,
            ["times"] = count,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupAdminAsync(string conversationId, long targetUin, bool enable)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_admin", new JsonObject
        {
            ["group_id"] = peer,
            ["user_id"] = targetUin,
            ["enable"] = enable,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupBanAsync(string conversationId, long targetUin, int durationSeconds)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_ban", new JsonObject
        {
            ["group_id"] = peer,
            ["user_id"] = targetUin,
            ["duration"] = durationSeconds,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupWholeBanAsync(string conversationId, bool enable)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_whole_ban", new JsonObject
        {
            ["group_id"] = peer,
            ["enable"] = enable,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupKickAsync(string conversationId, long targetUin, bool rejectAddRequest)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_kick", new JsonObject
        {
            ["group_id"] = peer,
            ["user_id"] = targetUin,
            ["reject_add_request"] = rejectAddRequest,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetFriendRemarkAsync(long uin, string remark)
    {
        if (uin <= 0) return (null, "bad-uin");
        var (_, err) = await _api.CallAsync("set_friend_remark", new JsonObject
        {
            ["user_id"] = uin,
            ["remark"] = remark ?? "",
        });
        if (err != null) return (null, err);

        lock (_gate)
        {
            foreach (var c in _contacts)
            {
                if (c is not JsonObject o) continue;
                if (NapCatApiClient.ReadLong(o["uin"]) != uin) continue;
                o["remark"] = remark ?? "";
                var nick = NapCatApiClient.ReadStr(o["name"]);
                var display = PreferDisplayName(card: "", remark ?? "", nick, qid: "", uin);
                var convId = "f" + uin;
                foreach (var row in _conversations)
                {
                    if (row is JsonObject co && NapCatApiClient.ReadStr(co["id"]) == convId)
                        co["title"] = display;
                }
                break;
            }
        }
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> DeleteFriendAsync(long uin, bool tempBlock, bool bothDel)
    {
        if (uin <= 0) return (null, "bad-uin");
        var (_, err) = await _api.CallAsync("delete_friend", new JsonObject
        {
            ["user_id"] = uin,
            ["friend_id"] = uin,
            ["temp_block"] = tempBlock,
            ["temp_both_del"] = bothDel,
        });
        if (err != null) return (null, err);

        lock (_gate)
        {
            _contacts.RemoveAll(c => c is JsonObject o && NapCatApiClient.ReadLong(o["uin"]) == uin);
            var convId = "f" + uin;
            _conversations.RemoveAll(c => c is JsonObject o && NapCatApiClient.ReadStr(o["id"]) == convId);
            _messages.Remove(convId);
        }
        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "sessionDataUpdated",
            ["data"] = new JsonObject { ["uin"] = _selfUin },
        }.ToJsonString());
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetSelfProfileAsync(string? nickname, string? signature)
    {
        var nick = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        var hasSig = signature != null;
        if (nick == null && !hasSig) return (null, "empty-profile");

        // Prefer dedicated longnick API for signature; set_qq_profile for nickname.
        if (nick != null)
        {
            var profileParams = new JsonObject { ["nickname"] = nick };
            if (hasSig) profileParams["personal_note"] = signature;
            var (_, err) = await _api.CallAsync("set_qq_profile", profileParams);
            if (err != null) return (null, err);
            lock (_gate) _selfNickname = nick;
        }

        if (hasSig)
        {
            var (_, err) = await _api.CallAsync("set_self_longnick", new JsonObject
            {
                ["longNick"] = signature ?? "",
            });
            if (err != null)
            {
                // Nickname may already have applied; still surface signature error.
                return (null, err);
            }
            else
            {
                lock (_gate) _selfSignature = signature ?? "";
            }
        }

        return (new JsonObject
        {
            ["ok"] = true,
            ["nickname"] = _selfNickname,
            ["signature"] = _selfSignature,
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetOnlineStatusAsync(int status, int extStatus, int batteryStatus)
    {
        var (_, err) = await _api.CallAsync("set_online_status", new JsonObject
        {
            ["status"] = status,
            ["ext_status"] = extStatus,
            ["battery_status"] = batteryStatus,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true, ["status"] = status }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupNoticesAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (raw, err) = await _api.CallAsync("_get_group_notice", new JsonObject { ["group_id"] = peer });
        if (err != null) return (null, err);

        var notices = new JsonArray();
        JsonArray? arr = raw as JsonArray
            ?? raw?["notices"] as JsonArray
            ?? raw?["data"] as JsonArray
            ?? raw?["notice"] as JsonArray;
        if (arr != null)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var id = NapCatApiClient.ReadStr(o["notice_id"] ?? o["id"] ?? o["fid"]);
                var content = "";
                if (o["message"] is JsonObject msg)
                    content = NapCatApiClient.ReadStr(msg["text"] ?? msg["content"]);
                if (string.IsNullOrEmpty(content))
                    content = NapCatApiClient.ReadStr(o["content"] ?? o["text"] ?? o["msg"]);
                notices.Add(new JsonObject
                {
                    ["id"] = id,
                    ["content"] = content,
                    ["time"] = NapCatApiClient.ReadLong(o["publish_time"] ?? o["time"] ?? o["send_time"]),
                });
            }
        }

        // Keep conversation announcement in sync with latest notice text.
        if (notices.Count > 0 && notices[0] is JsonObject first)
        {
            var text = NapCatApiClient.ReadStr(first["content"]);
            if (!string.IsNullOrEmpty(text))
            {
                lock (_gate)
                {
                    var row = _conversations.OfType<JsonObject>()
                        .FirstOrDefault(c => NapCatApiClient.ReadStr(c["id"]) == conversationId);
                    if (row != null) row["announcement"] = text;
                }
            }
        }

        return (new JsonObject { ["notices"] = notices }, null);
    }

    public async Task<(JsonObject? data, string? error)> SendGroupNoticeAsync(string conversationId, string content)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(content)) return (null, "empty-content");
        var (_, err) = await _api.CallAsync("_send_group_notice", new JsonObject
        {
            ["group_id"] = peer,
            ["content"] = content.Trim(),
        });
        if (err != null) return (null, err);
        lock (_gate)
        {
            var row = _conversations.OfType<JsonObject>()
                .FirstOrDefault(c => NapCatApiClient.ReadStr(c["id"]) == conversationId);
            if (row != null) row["announcement"] = content.Trim();
        }
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> DeleteGroupNoticeAsync(string conversationId, string noticeId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(noticeId)) return (null, "empty-notice-id");
        var (_, err) = await _api.CallAsync("_del_group_notice", new JsonObject
        {
            ["group_id"] = peer,
            ["notice_id"] = noticeId,
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupFilesAsync(string conversationId, string? folderId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");

        JsonNode? raw;
        string? err;
        if (string.IsNullOrWhiteSpace(folderId))
        {
            (raw, err) = await _api.CallAsync("get_group_root_files", new JsonObject
            {
                ["group_id"] = peer,
                ["file_count"] = 100,
            });
        }
        else
        {
            (raw, err) = await _api.CallAsync("get_group_files_by_folder", new JsonObject
            {
                ["group_id"] = peer,
                ["folder_id"] = folderId,
                ["folder"] = folderId,
                ["file_count"] = 100,
            });
        }
        if (err != null) return (null, err);

        var files = new JsonArray();
        var folders = new JsonArray();
        var fileArr = raw?["files"] as JsonArray ?? raw?["file_list"] as JsonArray;
        var folderArr = raw?["folders"] as JsonArray ?? raw?["folder_list"] as JsonArray;

        if (fileArr != null)
        {
            foreach (var n in fileArr)
            {
                if (n is not JsonObject o) continue;
                files.Add(new JsonObject
                {
                    ["fileId"] = NapCatApiClient.ReadStr(o["file_id"] ?? o["fileId"]),
                    ["name"] = NapCatApiClient.ReadStr(o["file_name"] ?? o["name"]),
                    ["size"] = NapCatApiClient.ReadLong(o["file_size"] ?? o["size"]),
                    ["busid"] = NapCatApiClient.ReadLong(o["busid"] ?? o["bus_id"]),
                    ["uploader"] = NapCatApiClient.ReadStr(o["uploader_name"] ?? o["uploader"]),
                    ["isFolder"] = false,
                });
            }
        }
        if (folderArr != null)
        {
            foreach (var n in folderArr)
            {
                if (n is not JsonObject o) continue;
                folders.Add(new JsonObject
                {
                    ["folderId"] = NapCatApiClient.ReadStr(o["folder_id"] ?? o["folderId"] ?? o["id"]),
                    ["name"] = NapCatApiClient.ReadStr(o["folder_name"] ?? o["name"]),
                    ["isFolder"] = true,
                });
            }
        }

        return (new JsonObject
        {
            ["files"] = files,
            ["folders"] = folders,
            ["folderId"] = folderId ?? "",
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupFileUrlAsync(string conversationId, string fileId, int busid)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(fileId)) return (null, "empty-file-id");
        var parameters = new JsonObject
        {
            ["group_id"] = peer,
            ["file_id"] = fileId,
        };
        if (busid != 0) parameters["busid"] = busid;
        var (raw, err) = await _api.CallAsync("get_group_file_url", parameters);
        if (err != null) return (null, err);
        var url = NapCatApiClient.ReadStr(raw?["url"] ?? raw?["file"]);
        if (string.IsNullOrEmpty(url)) return (null, "no-url");
        return (new JsonObject { ["url"] = url }, null);
    }

    public async Task<(JsonObject? data, string? error)> MarkAllAsReadAsync()
    {
        var (_, err) = await _api.CallAsync("_mark_all_as_read");
        if (err != null) return (null, err);
        lock (_gate)
        {
            foreach (var row in _conversations)
            {
                if (row is not JsonObject o) continue;
                o["unread"] = 0;
                var id = NapCatApiClient.ReadStr(o["id"]);
                if (!string.IsNullOrEmpty(id) && _convPrefs.TryGetValue(id, out var pref))
                    pref.Unread = 0;
            }
            SavePrefs();
        }
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetEssenceListAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (raw, err) = await _api.CallAsync("get_essence_msg_list", new JsonObject { ["group_id"] = peer });
        if (err != null) return (null, err);
        var list = new JsonArray();
        var arr = raw as JsonArray ?? raw?["messages"] as JsonArray ?? raw?["data"] as JsonArray;
        if (arr != null)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                list.Add(new JsonObject
                {
                    ["messageId"] = NapCatApiClient.ReadStr(o["message_id"] ?? o["msg_id"] ?? o["message_seq"]),
                    ["senderName"] = NapCatApiClient.ReadStr(o["sender_nick"] ?? o["sender_name"] ?? o["nickname"]),
                    ["senderUin"] = NapCatApiClient.ReadLong(o["sender_id"] ?? o["user_id"]),
                    ["content"] = NapCatApiClient.ReadStr(o["content"] ?? o["message_content"] ?? o["text"]),
                    ["time"] = NapCatApiClient.ReadLong(o["sender_time"] ?? o["time"]),
                });
            }
        }
        return (new JsonObject { ["messages"] = list }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetEssenceAsync(string messageId, bool set)
    {
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return (null, "bad-message-id");
        var action = set ? "set_essence_msg" : "delete_essence_msg";
        var (_, err) = await _api.CallAsync(action, new JsonObject { ["message_id"] = mid });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true, ["set"] = set }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupHonorAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (raw, err) = await _api.CallAsync("get_group_honor_info", new JsonObject
        {
            ["group_id"] = peer,
            ["type"] = "all",
        });
        if (err != null) return (null, err);
        return (new JsonObject { ["ok"] = true, ["honor"] = raw?.DeepClone() }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupShutListAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (raw, err) = await _api.CallAsync("get_group_shut_list", new JsonObject { ["group_id"] = peer });
        if (err != null) return (null, err);
        var list = new JsonArray();
        var arr = raw as JsonArray ?? raw?["list"] as JsonArray ?? raw?["data"] as JsonArray;
        if (arr != null)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                list.Add(new JsonObject
                {
                    ["uin"] = NapCatApiClient.ReadLong(o["user_id"] ?? o["uin"]),
                    ["name"] = NapCatApiClient.ReadStr(o["nickname"] ?? o["card"] ?? o["name"]),
                    ["shutUntil"] = NapCatApiClient.ReadLong(o["shut_up_timestamp"] ?? o["t"] ?? o["time"]),
                });
            }
        }
        return (new JsonObject { ["members"] = list }, null);
    }

    public async Task<(JsonObject? data, string? error)> GroupSignAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("send_group_sign", new JsonObject { ["group_id"] = peer });
        if (err != null)
            (_, err) = await _api.CallAsync("set_group_sign", new JsonObject { ["group_id"] = peer });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupPortraitAsync(string conversationId, string imageBase64)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(imageBase64)) return (null, "empty-image");
        var path = TryWriteTempMedia(imageBase64, ".jpg");
        try
        {
            var fileRef = path ?? ("base64://" + StripDataUrl(imageBase64));
            var (_, err) = await _api.CallAsync("set_group_portrait", new JsonObject
            {
                ["group_id"] = peer,
                ["file"] = fileRef,
                ["cache"] = 1,
            });
            return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
        }
        finally
        {
            if (path != null)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            }
        }
    }

    public async Task<(JsonObject? data, string? error)> SetGroupRemarkAsync(string conversationId, string remark)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (_, err) = await _api.CallAsync("set_group_remark", new JsonObject
        {
            ["group_id"] = peer,
            ["remark"] = remark ?? "",
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> CreateGroupFolderAsync(string conversationId, string name)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(name)) return (null, "empty-name");
        var (_, err) = await _api.CallAsync("create_group_file_folder", new JsonObject
        {
            ["group_id"] = peer,
            ["folder_name"] = name.Trim(),
            ["name"] = name.Trim(),
        });
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> DeleteGroupFileAsync(string conversationId, string fileId, int busid)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        if (string.IsNullOrWhiteSpace(fileId)) return (null, "empty-file-id");
        var p = new JsonObject { ["group_id"] = peer, ["file_id"] = fileId };
        if (busid != 0) p["busid"] = busid;
        var (_, err) = await _api.CallAsync("delete_group_file", p);
        return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> FetchPttTextAsync(string messageId)
    {
        // Prefer file id from cached message elements; fall back to message_id.
        string file = "";
        lock (_gate)
        {
            if (_msgIndex.TryGetValue(messageId, out var wire))
            {
                if (wire["elements"] is JsonArray els)
                {
                    foreach (var el in els)
                    {
                        if (el is not JsonObject eo) continue;
                        if (!string.Equals(NapCatApiClient.ReadStr(eo["Type"]), "Record", StringComparison.OrdinalIgnoreCase))
                            continue;
                        file = NapCatApiClient.ReadStr(eo["Url"] ?? eo["url"] ?? eo["file"]);
                        if (!string.IsNullOrEmpty(file)) break;
                    }
                }
            }
        }
        var mid = ExtractNapCatMessageId(messageId);
        var parameters = new JsonObject();
        if (!string.IsNullOrEmpty(file)) parameters["file"] = file;
        if (mid > 0) parameters["message_id"] = mid;
        var (raw, err) = await _api.CallAsync("fetch_ptt_text", parameters);
        if (err != null) return (null, err);
        var text = NapCatApiClient.ReadStr(raw?["text"] ?? raw?["result"] ?? raw?["content"]);
        return (new JsonObject { ["text"] = text, ["ok"] = !string.IsNullOrEmpty(text) }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetProfileLikeAsync(long? uin = null)
    {
        var parameters = new JsonObject();
        if (uin is > 0) parameters["user_id"] = uin.Value;
        var (raw, err) = await _api.CallAsync("get_profile_like", parameters.Count > 0 ? parameters : null);
        if (err != null) return (null, err);
        return (new JsonObject
        {
            ["ok"] = true,
            ["like"] = raw?.DeepClone() ?? new JsonObject(),
            ["total"] = NapCatApiClient.ReadLong(raw?["total_like_count"] ?? raw?["total"] ?? raw?["count"]),
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetUserStatusAsync(long uin)
    {
        if (uin <= 0) return (null, "bad-uin");
        var (raw, err) = await _api.CallAsync("nc_get_user_status", new JsonObject { ["user_id"] = uin });
        if (err != null) return (null, err);
        return (new JsonObject
        {
            ["ok"] = true,
            ["status"] = NapCatApiClient.ReadLong(raw?["status"] ?? raw?["online_status"]),
            ["extStatus"] = NapCatApiClient.ReadLong(raw?["ext_status"]),
            ["raw"] = raw?.DeepClone(),
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetVersionInfoAsync()
    {
        var (ver, err1) = await _api.CallAsync("get_version_info");
        var (st, err2) = await _api.CallAsync("get_status");
        if (err1 != null && err2 != null) return (null, err1);
        return (new JsonObject
        {
            ["ok"] = true,
            ["version"] = ver?.DeepClone(),
            ["status"] = st?.DeepClone(),
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupAtAllRemainAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return (null, "not-a-group");
        var (raw, err) = await _api.CallAsync("get_group_at_all_remain", new JsonObject { ["group_id"] = peer });
        if (err != null) return (null, err);
        return (new JsonObject
        {
            ["ok"] = true,
            ["canAtAll"] = NapCatApiClient.ReadLong(raw?["can_at_all"] ?? raw?["canAtAll"]) != 0
                || string.Equals(NapCatApiClient.ReadStr(raw?["can_at_all"]), "true", StringComparison.OrdinalIgnoreCase),
            ["remain"] = NapCatApiClient.ReadLong(raw?["remain"] ?? raw?["at_all_remain"] ?? raw?["remainAtAllCount"]),
            ["raw"] = raw?.DeepClone(),
        }, null);
    }

}
