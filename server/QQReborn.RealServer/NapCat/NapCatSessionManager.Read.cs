using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

public sealed partial class NapCatSessionManager
{
    // ---- queries ----

    public JsonObject GetSelf()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["uin"] = _selfUin,
                ["nickname"] = _selfNickname,
                ["avatarPath"] = _selfUin > 0 ? FriendAvatarUrl(_selfUin) : "",
                ["signature"] = _selfSignature,
                ["level"] = 0,
                ["backend"] = BackendId,
            };
        }
    }

    public JsonArray GetConversations()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var c in _conversations
                .OrderByDescending(c => IsTruthy(c, "isPinned"))
                .ThenByDescending(c => (string?)c["lastTime"] ?? ""))
                arr.Add(Clone(c));
            return arr;
        }
    }

    public Task<JsonArray> GetContactsAsync()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var c in _contacts) arr.Add(Clone(c));
            return Task.FromResult(arr);
        }
    }

    public async Task<JsonArray> GetMessagesAsync(string conversationId, bool allowCloudBackfill = true)
    {
        List<JsonObject> snapshot;
        lock (_gate)
        {
            if (!_messages.TryGetValue(conversationId, out var list) || list.Count == 0)
                snapshot = new List<JsonObject>();
            else
                snapshot = list.Select(Clone).ToList();
        }

        if (snapshot.Count == 0 && allowCloudBackfill && _online)
        {
            await TryPullHistoryAsync(conversationId, count: 20, beforeId: null);
            lock (_gate)
            {
                if (_messages.TryGetValue(conversationId, out var list))
                    snapshot = list.Select(Clone).ToList();
            }
        }

        var arr = new JsonArray();
        foreach (var m in snapshot) arr.Add(m);
        return arr;
    }

    private async Task<(int pageCount, int added)> TryPullHistoryAsync(string conversationId, int count, string? beforeId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer)) return (0, 0);
        count = count <= 0 ? 20 : Math.Min(count, 50);

        var historyGate = _historyGates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await historyGate.WaitAsync();
        try
        {
            return await TryPullHistoryCoreAsync(conversationId, kind, peer, count, beforeId);
        }
        finally { historyGate.Release(); }
    }

    private async Task<(int pageCount, int added)> TryPullHistoryCoreAsync(
        string conversationId, char kind, long peer, int count, string? beforeId)
    {

        // NapCat: get_*_msg_history returns a window of messages.
        // To page OLDER than an anchor, pass reverseOrder=true and message_seq=anchor message_id
        // (NapCat sets message_seq == message_id; real_seq is the dense chat sequence).
        var parameters = new JsonObject { ["count"] = count };
        string action;
        if (kind == 'g')
        {
            action = "get_group_msg_history";
            parameters["group_id"] = peer;
        }
        else
        {
            action = "get_friend_msg_history";
            parameters["user_id"] = peer;
        }

        if (!string.IsNullOrEmpty(beforeId))
        {
            var anchorNapCatId = ExtractNapCatMessageId(beforeId);
            if (anchorNapCatId > 0)
            {
                parameters["message_seq"] = anchorNapCatId;
                parameters["reverseOrder"] = true;
            }
        }

        var (data, err) = await _api.CallAsync(action, parameters);
        if (err != null && kind == 'f')
        {
            // Some builds only expose the generic name.
            (data, err) = await _api.CallAsync("get_msg_history", parameters);
        }
        if (err != null)
        {
            Console.WriteLine($"[NapCat] history {action}: {err}");
            return (0, 0);
        }

        var messages = data as JsonArray
            ?? data?["messages"] as JsonArray
            ?? data?["message"] as JsonArray;
        if (messages == null || messages.Count == 0)
        {
            Console.WriteLine($"[NapCat] history {action}: empty page beforeId={beforeId}");
            return (0, 0);
        }

        var added = 0;
        foreach (var n in messages)
        {
            if (n is not JsonObject o) continue;
            if (o["message_type"] == null)
                o["message_type"] = kind == 'g' ? "group" : "private";
            if (kind == 'g' && o["group_id"] == null) o["group_id"] = peer;
            if (kind == 'f' && o["user_id"] == null) o["user_id"] = peer;
            // History rows: private self-sent messages have user_id=self. Force the
            // conversation peer so id becomes f{peer}:{mid}, not f{self}:{mid}.
            var senderUin = NapCatApiClient.ReadLong((o["sender"] as JsonObject)?["user_id"]);
            if (senderUin <= 0) senderUin = NapCatApiClient.ReadLong(o["user_id"]);
            var isSelf = _selfUin > 0 && senderUin == _selfUin;
            long? forcedPeer = kind == 'f' ? peer : null;
            var wire = MapIncomingMessage(o, isSentEcho: isSelf, forcedPrivatePeer: forcedPeer);
            if (wire == null) continue;
            lock (_gate)
            {
                if (!_messages.TryGetValue(conversationId, out var list))
                {
                    list = new List<JsonObject>();
                    _messages[conversationId] = list;
                }
                var id = (string)wire["id"]!;
                if (list.Any(m => (string?)m["id"] == id)) continue;
                InsertMessageInTimeOrder(list, wire);
                _msgIndex[id] = wire;
                added++;
            }
        }

        lock (_gate)
        {
            if (_messages.TryGetValue(conversationId, out var list))
            {
                _messages[conversationId] = list
                    .OrderBy(m => (string?)m["time"] ?? "", StringComparer.Ordinal)
                    .ToList();
            }
        }
        Console.WriteLine($"[NapCat] history {action}: merged +{added}/{messages.Count} beforeId={beforeId}");
        return (messages.Count, added);
    }

    public async Task<JsonArray> GetGroupMembersAsync(string conversationId)
    {
        var arr = new JsonArray();
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return arr;

        var (data, err) = await _api.CallAsync("get_group_member_list", new JsonObject
        {
            ["group_id"] = peer,
            ["no_cache"] = true,
        });
        if (err != null)
        {
            Console.WriteLine("[NapCat] get_group_member_list: " + err);
            return arr;
        }
        var members = data as JsonArray ?? data?["members"] as JsonArray;
        if (members == null) return arr;

        var rows = new List<JsonObject>();
        foreach (var n in members)
        {
            if (n is not JsonObject o) continue;
            var uin = NapCatApiClient.ReadLong(o["user_id"] ?? o["uin"]);
            if (uin <= 0) continue;
            var card = NapCatApiClient.ReadStr(o["card"]);
            var nickname = NapCatApiClient.ReadStr(o["nickname"] ?? o["nick"]);
            var title = NapCatApiClient.ReadStr(o["title"]);
            var roleRaw = NapCatApiClient.ReadStr(o["role"]);
            var role = MapRoleLabel(roleRaw);

            // UUID-looking nicks (e.g. NapCat session id mistakenly set as QQ nick) → try qid.
            var qid = "";
            if (LooksLikeOpaqueId(nickname) || LooksLikeOpaqueId(card))
                qid = await TryGetQidAsync(uin);

            var name = PreferDisplayName(card, remark: "", nickname, qid, uin);
            rows.Add(new JsonObject
            {
                ["uin"] = uin,
                ["name"] = name,
                ["nickname"] = nickname,
                ["card"] = card,
                ["avatarPath"] = FriendAvatarUrl(uin),
                ["role"] = role,
                ["roleRaw"] = roleRaw,
                ["title"] = title,
            });
        }

        // 群主 → 管理 → 其余，同级按名称
        foreach (var row in rows
                     .OrderBy(r => RoleSortKey(NapCatApiClient.ReadStr(r["roleRaw"])))
                     .ThenBy(r => NapCatApiClient.ReadStr(r["name"]), StringComparer.OrdinalIgnoreCase))
            arr.Add(row);

        Console.WriteLine($"[NapCat] getGroupMembers g{peer} count={arr.Count}");
        return arr;
    }

    public JsonArray GetFriendRequests()
    {
        // Refresh best-effort from NapCat doubt list (primary list API unsupported).
        _ = RefreshFriendRequestsFromApiAsync();
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var r in _friendRequests) arr.Add(Clone(r));
            return arr;
        }
    }

    public JsonObject AcceptFriendRequest(long uin)
    {
        // Sync wrapper for interface; real work is fire-and-forget + flag map.
        if (!_friendReqFlagByUin.TryGetValue(uin.ToString(), out var flag) || string.IsNullOrEmpty(flag))
        {
            // Try any request row
            lock (_gate)
            {
                var row = _friendRequests.FirstOrDefault(r => NapCatApiClient.ReadLong(r["uin"]) == uin);
                flag = NapCatApiClient.ReadStr(row?["flag"]);
            }
        }
        if (string.IsNullOrEmpty(flag))
            return new JsonObject { ["ok"] = false, ["handled"] = false, ["reason"] = "no-pending-flag; approve in NTQQ or wait for request event" };

        try
        {
            var (_, err) = _api.CallAsync("set_friend_add_request", new JsonObject
            {
                ["flag"] = flag,
                ["approve"] = true,
                ["remark"] = "",
            }).GetAwaiter().GetResult();
            if (err != null)
                return new JsonObject { ["ok"] = false, ["handled"] = false, ["reason"] = err };
            lock (_gate)
            {
                foreach (var r in _friendRequests.Where(r => NapCatApiClient.ReadLong(r["uin"]) == uin))
                    r["handled"] = true;
            }
            _friendReqFlagByUin.Remove(uin.ToString());
            return new JsonObject { ["ok"] = true, ["handled"] = true };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["handled"] = false, ["reason"] = ex.Message };
        }
    }

    public async Task<JsonObject> RejectFriendRequestAsync(long uin)
    {
        if (!_friendReqFlagByUin.TryGetValue(uin.ToString(), out var flag) || string.IsNullOrEmpty(flag))
        {
            lock (_gate)
            {
                var row = _friendRequests.FirstOrDefault(r => NapCatApiClient.ReadLong(r["uin"]) == uin);
                flag = NapCatApiClient.ReadStr(row?["flag"]);
            }
        }
        if (string.IsNullOrEmpty(flag))
            return new JsonObject { ["ok"] = false, ["handled"] = false, ["reason"] = "no-pending-flag" };

        var (_, err) = await _api.CallAsync("set_friend_add_request", new JsonObject
        {
            ["flag"] = flag,
            ["approve"] = false,
            ["remark"] = "",
        });
        if (err != null)
            return new JsonObject { ["ok"] = false, ["handled"] = false, ["reason"] = err };

        lock (_gate)
        {
            _friendRequests.RemoveAll(r => NapCatApiClient.ReadLong(r["uin"]) == uin);
        }
        _friendReqFlagByUin.Remove(uin.ToString());
        return new JsonObject { ["ok"] = true, ["handled"] = true };
    }

    private async Task RefreshFriendRequestsFromApiAsync()
    {
        try
        {
            var (data, err) = await _api.CallAsync("get_doubt_friends_add_request");
            if (err != null || data is not JsonArray arr) return;
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var uin = NapCatApiClient.ReadLong(o["user_id"] ?? o["uin"]);
                var flag = NapCatApiClient.ReadStr(o["flag"] ?? o["request_id"]);
                if (uin <= 0) continue;
                if (!string.IsNullOrEmpty(flag)) _friendReqFlagByUin[uin.ToString()] = flag;
                lock (_gate)
                {
                    if (_friendRequests.Any(r => NapCatApiClient.ReadLong(r["uin"]) == uin)) continue;
                    _friendRequests.Add(new JsonObject
                    {
                        ["uin"] = uin,
                        ["name"] = PreferDisplayName("", "", NapCatApiClient.ReadStr(o["nickname"]), "", uin),
                        ["avatarPath"] = FriendAvatarUrl(uin),
                        ["message"] = NapCatApiClient.ReadStr(o["source"] ?? o["message"] ?? o["comment"]),
                        ["handled"] = false,
                        ["flag"] = flag,
                    });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] refresh friend req: " + ex.Message); }
    }

    public async Task<(JsonObject? data, string? error)> GetUserProfileAsync(long uin)
    {
        var (data, err) = await _api.CallAsync("get_stranger_info", new JsonObject { ["user_id"] = uin });
        if (err != null)
        {
            return (new JsonObject
            {
                ["uin"] = uin,
                ["nickname"] = uin.ToString(),
                ["avatarPath"] = FriendAvatarUrl(uin),
                ["signature"] = "",
            }, null);
        }
        var nick = NapCatApiClient.ReadStr(data?["nickname"] ?? data?["nick"]);
        var qid = NapCatApiClient.ReadStr(data?["qid"]);
        var display = PreferDisplayName("", "", nick, qid, uin);
        return (new JsonObject
        {
            ["uin"] = uin,
            ["nickname"] = display,
            ["qid"] = qid,
            ["avatarPath"] = FriendAvatarUrl(uin),
            ["signature"] = NapCatApiClient.ReadStr(data?["longNick"] ?? data?["long_nick"] ?? data?["sign"]),
            ["sex"] = NapCatApiClient.ReadStr(data?["sex"]),
            ["age"] = NapCatApiClient.ReadLong(data?["age"]),
        }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetEarlierMessagesAsync(string conversationId, string? beforeId, int count)
    {
        count = count > 0 ? Math.Min(count, 50) : 20;
        // Always ask NapCat for a page. reverseOrder+message_seq is applied when beforeId set.
        var history = await TryPullHistoryAsync(conversationId, count, beforeId);

        JsonArray messages;
        lock (_gate)
        {
            messages = new JsonArray();
            if (_messages.TryGetValue(conversationId, out var list) && list.Count > 0)
            {
                IEnumerable<JsonObject> older = list;
                if (!string.IsNullOrEmpty(beforeId))
                {
                    var idx = list.FindIndex(m => (string?)m["id"] == beforeId);
                    if (idx > 0)
                        older = list.Take(idx); // strictly older than anchor
                    else if (idx == 0)
                        older = Array.Empty<JsonObject>(); // anchor is oldest we have after pull
                    else
                    {
                        // Anchor not in cache — return the oldest page we hold.
                        older = list;
                    }
                }
                // Closest older page to the anchor (or newest page if no anchor).
                foreach (var m in older.Reverse().Take(count).Reverse())
                    messages.Add(Clone(m));
            }
        }

        // Stop when NapCat page empty/short, or full page produced only duplicates.
        var hasMore = history.pageCount >= count
            && (history.added > 0 || string.IsNullOrEmpty(beforeId));
        if (messages.Count == 0) hasMore = false;
        return (new JsonObject { ["messages"] = messages, ["hasMore"] = hasMore }, null);
    }

    public async Task<JsonObject> RecallMessageAsync(string conversationId, string messageId)
    {
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return new JsonObject { ["ok"] = false, ["recalled"] = false, ["reason"] = "invalid-message-id" };
        var (data, err) = await _api.CallAsync("delete_msg", new JsonObject { ["message_id"] = mid });
        if (err != null) return new JsonObject { ["ok"] = false, ["recalled"] = false, ["reason"] = err };
        return new JsonObject { ["ok"] = true, ["recalled"] = true, ["data"] = data?.DeepClone() };
    }

    public async Task<JsonObject> QuitGroupAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return new JsonObject { ["ok"] = false, ["left"] = false, ["reason"] = "not-a-group" };
        var (_, err) = await _api.CallAsync("set_group_leave", new JsonObject { ["group_id"] = peer });
        if (err != null) return new JsonObject { ["ok"] = false, ["left"] = false, ["reason"] = err };
        lock (_gate) _conversations.RemoveAll(c => (string?)c["id"] == conversationId);
        return new JsonObject { ["ok"] = true, ["left"] = true };
    }

    public async Task<(JsonObject? data, string? error)> SendNudgeAsync(string conversationId, long targetUin)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");
        // NapCat: group_poke / friend_poke (names vary)
        if (kind == 'g')
        {
            var (_, err) = await _api.CallAsync("group_poke", new JsonObject
            {
                ["group_id"] = peer,
                ["user_id"] = targetUin > 0 ? targetUin : peer,
            });
            if (err != null)
            {
                (_, err) = await _api.CallAsync("send_group_msg", new JsonObject
                {
                    ["group_id"] = peer,
                    ["message"] = "[戳一戳]",
                });
                if (err != null) return (null, err);
            }
        }
        else
        {
            var (_, err) = await _api.CallAsync("friend_poke", new JsonObject { ["user_id"] = peer });
            if (err != null) return (null, err);
        }
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetAvatarAsync(string imageBase64)
    {
        if (string.IsNullOrWhiteSpace(imageBase64)) return (null, "empty-image");
        var path = TryWriteTempMedia(imageBase64, ".jpg");
        if (path == null) return (null, "temp-write-failed");
        try
        {
            var (_, err) = await _api.CallAsync("set_qq_avatar", new JsonObject { ["file"] = path });
            if (err != null)
            {
                (_, err) = await _api.CallAsync("set_qq_avatar", new JsonObject { ["file"] = "base64://" + StripDataUrl(imageBase64) });
            }
            return err != null ? (null, err) : (new JsonObject { ["ok"] = true }, null);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public Task<(JsonObject? data, string? error)> GetMediaUrlAsync(string messageId)
    {
        if (_msgIndex.TryGetValue(messageId, out var wire))
        {
            var url = NapCatApiClient.ReadStr(wire["imagePath"]);
            if (string.IsNullOrEmpty(url) && wire["elements"] is JsonArray els)
            {
                foreach (var e in els)
                {
                    if (e is not JsonObject o) continue;
                    var t = NapCatApiClient.ReadStr(o["Type"] ?? o["type"]);
                    if (t is "Image" or "image" or "Video" or "video" or "Record" or "record" or "File" or "file")
                    {
                        url = NapCatApiClient.ReadStr(o["Url"] ?? o["url"]);
                        if (!string.IsNullOrEmpty(url)) break;
                    }
                }
            }
            return Task.FromResult<(JsonObject?, string?)>((new JsonObject { ["url"] = url }, null));
        }
        return Task.FromResult<(JsonObject?, string?)>((null, "message-not-found"));
    }

    public async Task<(JsonObject? data, string? error)> GetVoicePlayableAsync(string messageId)
    {
        // Prefer already-mapped record URL on the wire message.
        string url = "";
        if (_msgIndex.TryGetValue(messageId, out var wire) && wire["elements"] is JsonArray els)
        {
            foreach (var e in els)
            {
                if (e is not JsonObject o) continue;
                var t = NapCatApiClient.ReadStr(o["Type"] ?? o["type"]);
                if (t is "Record" or "record" or "Voice" or "voice")
                {
                    url = NapCatApiClient.ReadStr(o["Url"] ?? o["url"]);
                    if (!string.IsNullOrEmpty(url)) break;
                }
            }
        }
        if (string.IsNullOrEmpty(url))
            return (null, "voice-url-not-found");

        // Shell prefers audioBase64. Download http(s) when possible.
        try
        {
            var (data, err) = await _api.CallAsync("get_record", new JsonObject
            {
                ["file"] = url,
                ["out_format"] = "mp3",
            });
            if (err != null) return (new JsonObject { ["url"] = url, ["format"] = "path" }, null);
            var local = NapCatApiClient.ReadStr(data?["file"] ?? data?["url"]);
            if (!string.IsNullOrEmpty(local) && File.Exists(local))
            {
                var bytes = await File.ReadAllBytesAsync(local);
                return (new JsonObject
                {
                    ["audioBase64"] = Convert.ToBase64String(bytes),
                    ["format"] = "mp3",
                    ["duration"] = NapCatApiClient.ReadLong(data?["duration"]),
                    ["url"] = local,
                }, null);
            }
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length > 0)
                    return (new JsonObject { ["audioBase64"] = Convert.ToBase64String(bytes), ["format"] = "bin", ["duration"] = 0, ["url"] = url }, null);
            }
            return (new JsonObject { ["url"] = string.IsNullOrEmpty(local) ? url : local, ["format"] = "path" }, null);
        }
        catch (Exception ex)
        {
            return (new JsonObject { ["url"] = url, ["format"] = "path" }, "voice: " + ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> GetFavoriteStickersAsync(int count)
    {
        var (raw, err) = await _api.CallAsync("fetch_custom_face", new JsonObject
        {
            ["count"] = count > 0 ? count : 48,
        });
        if (err != null) return (null, err);
        var stickers = new JsonArray();
        var source = raw as JsonArray ?? raw?["data"] as JsonArray;
        if (source != null)
        {
            foreach (var item in source)
            {
                var value = item is JsonValue
                    ? NapCatApiClient.ReadStr(item)
                    : NapCatApiClient.ReadStr(item?["url"] ?? item?["file"] ?? item?["path"]);
                if (!string.IsNullOrWhiteSpace(value)) stickers.Add(value);
            }
        }
        return (new JsonObject { ["stickers"] = stickers }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetForwardDetailsAsync(string messageId)
    {
        if (!_msgIndex.TryGetValue(messageId, out var wire))
            return (null, "message-not-found");
        var forwardId = "";
        if (wire["elements"] is JsonArray elements)
        {
            foreach (var element in elements)
            {
                if (element is not JsonObject eo) continue;
                var type = NapCatApiClient.ReadStr(eo["Type"] ?? eo["type"]);
                if (string.Equals(type, "Forward", StringComparison.OrdinalIgnoreCase))
                {
                    forwardId = NapCatApiClient.ReadStr(eo["Url"] ?? eo["url"] ?? eo["id"]);
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(forwardId)) return (null, "forward-id-not-found");
        var (raw, err) = await _api.CallAsync("get_forward_msg", new JsonObject { ["message_id"] = forwardId });
        if (err != null) return (null, err);
        var source = raw as JsonArray ?? raw?["messages"] as JsonArray ?? raw?["content"] as JsonArray;
        var entries = new JsonArray();
        if (source != null)
        {
            foreach (var item in source)
            {
                if (item is not JsonObject row) continue;
                var sender = NapCatApiClient.ReadStr(row["sender"]?["nickname"]
                    ?? row["sender"]?["card"] ?? row["senderName"] ?? row["nickname"]);
                var content = row["message"] ?? row["content"];
                string text;
                string? image = null;
                if (content is JsonArray)
                {
                    var mapped = MapSegments(content);
                    text = mapped.text;
                    image = mapped.imagePath;
                }
                else text = NapCatApiClient.ReadStr(content);
                if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(image)) text = "[消息]";
                entries.Add(new JsonObject
                {
                    ["senderName"] = sender,
                    ["text"] = text,
                    ["imagePath"] = image,
                });
            }
        }
        return (new JsonObject { ["entries"] = entries }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetFileDownloadUrlAsync(string conversationId, string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return (null, "empty-file-id");
        // Already a URL?
        if (fileId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (new JsonObject { ["url"] = fileId }, null);

        // Group file URL first when conversation is a group.
        if (TryParseConv(conversationId, out var kind, out var peer))
        {
            if (kind == 'g')
            {
                var (gData, gErr) = await _api.CallAsync("get_group_file_url", new JsonObject
                {
                    ["group_id"] = peer,
                    ["file_id"] = fileId,
                });
                if (gErr == null)
                {
                    var gUrl = NapCatApiClient.ReadStr(gData?["url"] ?? gData?["file"]);
                    if (!string.IsNullOrEmpty(gUrl))
                        return (new JsonObject { ["url"] = gUrl }, null);
                }
            }
            else
            {
                var (pData, pErr) = await _api.CallAsync("get_private_file_url", new JsonObject
                {
                    ["user_id"] = peer,
                    ["file_id"] = fileId,
                });
                if (pErr == null)
                {
                    var pUrl = NapCatApiClient.ReadStr(pData?["url"] ?? pData?["file"]);
                    if (!string.IsNullOrEmpty(pUrl))
                        return (new JsonObject { ["url"] = pUrl }, null);
                }
            }
        }

        var (data, err) = await _api.CallAsync("get_file", new JsonObject { ["file_id"] = fileId });
        if (err != null)
        {
            (data, err) = await _api.CallAsync("get_file", new JsonObject { ["file"] = fileId });
        }
        if (err != null) return (null, err);
        var url = NapCatApiClient.ReadStr(data?["url"] ?? data?["file"]);
        if (string.IsNullOrEmpty(url)) return (null, "no-url");
        return (new JsonObject { ["url"] = url, ["file"] = NapCatApiClient.ReadStr(data?["file"]) }, null);
    }

    public async Task<(JsonObject? data, string? error)> GetGroupNotificationsAsync()
    {
        await RefreshGroupNotificationsFromApiAsync();
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var n in _groupNotifications) arr.Add(Clone(n));
            return (new JsonObject { ["notifications"] = arr }, null);
        }
    }

    public async Task<(JsonObject? data, string? error)> HandleGroupNotificationAsync(
        long groupUin, ulong sequence, string notifType, string operate, string? message, bool isFiltered)
    {
        var key = $"{groupUin}:{(long)sequence}";
        if (!_groupReqFlagByKey.TryGetValue(key, out var flag) || string.IsNullOrEmpty(flag))
        {
            lock (_gate)
            {
                var row = _groupNotifications.FirstOrDefault(n =>
                    NapCatApiClient.ReadLong(n["groupUin"]) == groupUin
                    && (ulong)NapCatApiClient.ReadLong(n["sequence"]) == sequence);
                flag = NapCatApiClient.ReadStr(row?["flag"]);
            }
        }
        if (string.IsNullOrEmpty(flag))
            return (null, "no-request-flag");

        var approve = operate is "allow" or "accept" or "approve";
        var subType = string.Equals(notifType, "invite", StringComparison.OrdinalIgnoreCase) ? "invite" : "add";
        var (_, err) = await _api.CallAsync("set_group_add_request", new JsonObject
        {
            ["flag"] = flag,
            ["sub_type"] = subType,
            ["approve"] = approve,
            ["reason"] = message ?? "",
        });
        if (err != null) return (null, err);

        lock (_gate)
        {
            _groupNotifications.RemoveAll(n =>
                NapCatApiClient.ReadLong(n["groupUin"]) == groupUin
                && (ulong)NapCatApiClient.ReadLong(n["sequence"]) == sequence);
        }
        _groupReqFlagByKey.Remove(key);
        return (new JsonObject { ["ok"] = true }, null);
    }

    public async Task<(JsonObject? data, string? error)> SetGroupReactionAsync(
        string conversationId, string messageId, string code, bool isAdd)
    {
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return (null, "invalid-message-id");
        // NapCat: set_msg_emoji_like (emoji_id as string/codepoint)
        var emojiId = string.IsNullOrWhiteSpace(code) ? "128077" : code.Trim();
        var (_, err) = await _api.CallAsync("set_msg_emoji_like", new JsonObject
        {
            ["message_id"] = mid,
            ["emoji_id"] = emojiId,
            ["set"] = isAdd,
        });
        if (err != null)
        {
            // alternate param name
            (_, err) = await _api.CallAsync("set_msg_emoji_like", new JsonObject
            {
                ["message_id"] = mid,
                ["emoji_id"] = emojiId,
            });
        }
        if (err != null) return (null, err);
        return (new JsonObject { ["ok"] = true, ["messageId"] = messageId, ["code"] = emojiId, ["isAdd"] = isAdd }, null);
    }

    private async Task RefreshGroupNotificationsFromApiAsync()
    {
        try
        {
            var (data, err) = await _api.CallAsync("get_group_system_msg");
            if (err != null || data is not JsonObject root) return;

            void Ingest(JsonArray? arr, string type)
            {
                if (arr == null) return;
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    var groupId = NapCatApiClient.ReadLong(o["group_id"] ?? o["groupUin"] ?? o["group_uin"]);
                    var userId = NapCatApiClient.ReadLong(o["requester_uin"] ?? o["user_id"] ?? o["invitor_uin"] ?? o["actor_uin"]);
                    var flag = NapCatApiClient.ReadStr(o["flag"] ?? o["request_id"]);
                    var seq = NapCatApiClient.ReadLong(o["request_id"] ?? o["seq"] ?? o["id"]);
                    if (seq <= 0) seq = groupId ^ userId ^ DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var checkedDone = o["checked"] is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
                    if (checkedDone) continue;
                    if (!string.IsNullOrEmpty(flag))
                    {
                        _groupReqFlagByKey[$"{groupId}:{seq}"] = flag;
                        _groupReqFlagByKey[flag] = flag;
                    }
                    lock (_gate)
                    {
                        if (_groupNotifications.Any(x =>
                                NapCatApiClient.ReadLong(x["groupUin"]) == groupId
                                && NapCatApiClient.ReadLong(x["sequence"]) == seq))
                            continue;
                        _groupNotifications.Add(new JsonObject
                        {
                            ["groupUin"] = groupId,
                            ["sequence"] = seq,
                            ["type"] = type,
                            ["message"] = NapCatApiClient.ReadStr(o["message"] ?? o["comment"]),
                            ["initiatorNickname"] = PreferDisplayName(
                                "", "", NapCatApiClient.ReadStr(o["requester_nick"] ?? o["nickname"]), "", userId),
                            ["initiatorUin"] = userId,
                            ["avatarPath"] = userId > 0 ? FriendAvatarUrl(userId) : GroupAvatarUrl(groupId),
                            ["isFiltered"] = false,
                            ["flag"] = flag,
                        });
                    }
                }
            }

            Ingest(root["join_requests"] as JsonArray, "join");
            Ingest(root["invited_requests"] as JsonArray ?? root["InvitedRequest"] as JsonArray, "invite");
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] refresh group notif: " + ex.Message); }
    }

    public JsonObject GetSpaceFeed()
    {
        if (_qzone == null)
            return new JsonObject { ["moments"] = new JsonArray(), ["hasMore"] = false, ["backend"] = BackendId };

        // First open of 动态 often races the fire-and-forget login refresh. If still empty,
        // block once so the Shell button is not a permanent blank page.
        var snap = _qzone.Snapshot();
        if (snap["moments"] is JsonArray ma && ma.Count == 0)
        {
            try
            {
                _qzone.RefreshAsync(earlier: false).GetAwaiter().GetResult();
                snap = _qzone.Snapshot();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Qzone] GetSpaceFeed refresh: " + ex.Message);
            }
        }
        return snap;
    }

    public JsonObject SetSpaceLike(string momentId, bool isLiked)
    {
        if (_qzone == null) return new JsonObject { ["ok"] = false, ["reason"] = "not-online" };
        try
        {
            var ok = _qzone.LikeAsync(momentId, isLiked).GetAwaiter().GetResult();
            return new JsonObject { ["ok"] = ok };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    public JsonObject SetSpaceComment(string momentId, string text)
    {
        if (_qzone == null) return new JsonObject { ["ok"] = false, ["reason"] = "not-online" };
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject { ["ok"] = false, ["reason"] = "empty" };
        try
        {
            var ok = _qzone.CommentAsync(momentId, text).GetAwaiter().GetResult();
            return new JsonObject { ["ok"] = ok };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    public JsonObject IngestSpaceWebhook(JsonNode? body)
    {
        // Optional hybrid: allow POST /webhook/space to append local moments.
        if (body == null) return new JsonObject { ["ok"] = false, ["reason"] = "empty" };
        // Keep simple: not merging into Qzone client; Shell primarily uses live QZone.
        return new JsonObject { ["ok"] = true, ["note"] = "use live QZone feeds from NapCat cookies" };
    }

    public async Task FetchQzoneFeedNativeAsync()
    {
        if (_qzone == null) return;
        await _qzone.RefreshAsync(earlier: false);
        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "spaceFeedUpdated",
            ["data"] = _qzone.Snapshot(),
        }.ToJsonString());
    }

    public async Task<JsonObject> FetchEarlierSpaceFeedAsync(int num = 20)
    {
        if (_qzone == null) return new JsonObject { ["added"] = 0, ["hasMore"] = false };
        var (added, hasMore) = await _qzone.RefreshAsync(pageSize: num, earlier: true);
        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "spaceFeedUpdated",
            ["data"] = _qzone.Snapshot(),
        }.ToJsonString());
        return new JsonObject { ["added"] = added, ["hasMore"] = hasMore };
    }
}
