using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

public sealed partial class NapCatSessionManager
{
    // ---- account ----

    public async Task<(JsonObject? data, string? error)> ConfigureAccountAsync(string? expectedUin = null)
    {
        // configureAccount is also used after a Shell reconnect. Serialise it so two
        // overlapping requests cannot clear/repopulate the same cache out of order.
        await _configureGate.WaitAsync();
        try { return await ConfigureAccountCoreAsync(expectedUin); }
        finally { _configureGate.Release(); }
    }

    private async Task<(JsonObject? data, string? error)> ConfigureAccountCoreAsync(string? expectedUin)
    {
        // Optional expectedUin must match NapCat logged-in account when provided.
        try
        {
            var (data, err) = await _api.CallAsync("get_login_info");
            if (err != null) return (null, "无法连接 NapCat（" + err + "）。请确认 NTQQ+NapCat 已登录且 HTTP API 已开。");
            if (data == null) return (null, "get_login_info 无 data");

            var uin = NapCatApiClient.ReadLong(data["user_id"] ?? data["uin"]);
            var nick = NapCatApiClient.ReadStr(data["nickname"]);
            if (uin <= 0) return (null, "NapCat 未返回有效 QQ 号");

            // Shell may leave QQ empty — adopt NapCat's current account.
            // If Shell sent a number, it must match (prevents binding the wrong session).
            if (!string.IsNullOrWhiteSpace(expectedUin)
                && long.TryParse(expectedUin.Trim(), out var expect)
                && expect > 0
                && expect != uin)
            {
                return (null, $"NapCat 当前登录 {uin}，与客户端期望的 {expect} 不一致。清空 QQ 号或切换 NapCat 账号后再试。");
            }

            bool reload;
            lock (_gate)
            {
                reload = !_online || _selfUin != uin;
                _selfUin = uin;
                _selfNickname = nick;
                _online = true;
                if (reload)
                {
                    _messages.Clear();
                    _conversations.Clear();
                    _contacts.Clear();
                    _msgIndex.Clear();
                    LoadPrefs(uin);
                }
            }

            // Repeated configureAccount calls are normal during page reloads and
            // reconnects. Do not wipe a live transcript in that case. Populating
            // contacts/groups can involve many slow NapCat calls, so it must not
            // hold the account-bind request open until every list is complete.
            if (reload) StartPopulateLists(uin);
            EnsureEventLoop();
            if (reload || _qzone == null)
            {
                _qzone = new QzoneFeedClient(_api, uin);
                // Fire-and-forget first page of 好友动态
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _qzone.RefreshAsync();
                        Broadcast?.Invoke(new JsonObject
                        {
                            ["type"] = "spaceFeedUpdated",
                            ["data"] = _qzone.Snapshot(),
                        }.ToJsonString());
                    }
                    catch (Exception ex) { Console.WriteLine("[Qzone] initial fetch: " + ex.Message); }
                });
            }
            BroadcastLoginStatus("online", uin, nick);
            Console.WriteLine($"[NapCat] gateway online uin={uin} nick={nick}");
            return (new JsonObject
            {
                ["accepted"] = true,
                ["backend"] = BackendId,
                ["mode"] = "localGateway",
                ["uin"] = uin,
                ["nickname"] = nick,
                ["hint"] = "本机网关：出门请用 OpenFrp/Frp 映射 8765，见 docs/USER-GATEWAY-OPENFRP.md",
            }, null);
        }
        catch (Exception ex)
        {
            return (null, "configureAccount failed: " + ex.Message);
        }
    }

    private void StartPopulateLists(long uin)
    {
        lock (_gate)
        {
            if (_populateTask != null && !_populateTask.IsCompleted && _populateUin == uin)
                return;

            _populateUin = uin;
            _populateTask = Task.Run(async () =>
            {
                try
                {
                    await PopulateListsAsync();
                    Broadcast?.Invoke(new JsonObject
                    {
                        ["type"] = "sessionDataUpdated",
                        ["data"] = new JsonObject { ["uin"] = uin },
                    }.ToJsonString());
                    Console.WriteLine($"[NapCat] background lists ready uin={uin}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[NapCat] background list population: " + ex.Message);
                }
            });
        }
    }

    private void EnsureEventLoop()
    {
        if (_wsLoop != null && !_wsLoop.IsCompleted) return;
        _wsCts?.Cancel();
        _wsCts = new CancellationTokenSource();
        _wsLoop = Task.Run(() => EventLoopAsync(_wsCts.Token));
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        var delay = Math.Max(500, _opts.ReconnectDelayMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                var uri = new Uri(BuildWsUrl());
                Console.WriteLine($"[NapCat] connecting event WS {uri}");
                await ws.ConnectAsync(uri, ct);
                Console.WriteLine("[NapCat] event WS connected");

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            break;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    try { HandleEvent(text); }
                    catch (Exception ex) { Console.WriteLine("[NapCat] event handle: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[NapCat] event WS error: {ex.Message}; reconnect in {delay}ms");
            }

            try { await Task.Delay(delay, ct); } catch { break; }
        }
    }

    private string BuildWsUrl()
    {
        var url = _opts.EventWs;
        if (string.IsNullOrWhiteSpace(_opts.AccessToken)) return url;
        if (url.Contains("access_token", StringComparison.OrdinalIgnoreCase)) return url;
        return url + (url.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(_opts.AccessToken);
    }

    private void HandleEvent(string text)
    {
        var node = JsonNode.Parse(text) as JsonObject;
        if (node == null) return;
        var postType = NapCatApiClient.ReadStr(node["post_type"]);
        if (postType == "meta_event") return;

        if (postType == "message" || postType == "message_sent")
        {
            var wire = MapIncomingMessage(node, postType == "message_sent");
            if (wire == null) return;
            var convId = (string)wire["conversationId"]!;
            lock (_gate)
            {
                if (!_messages.TryGetValue(convId, out var list))
                {
                    list = new List<JsonObject>();
                    _messages[convId] = list;
                }
                var id = (string)wire["id"]!;
                if (list.Any(m => (string?)m["id"] == id)) return;
                InsertMessageInTimeOrder(list, wire);
                _msgIndex[id] = wire;
                BumpConversation(
                    convId,
                    (string?)wire["text"],
                    (string?)wire["direction"] == "Incoming",
                    ParseWireTime(wire));
            }
            Broadcast?.Invoke(new JsonObject
            {
                ["type"] = "messageReceived",
                ["data"] = Clone(wire),
            }.ToJsonString());
        }
        else if (postType == "notice")
        {
            var notice = NapCatApiClient.ReadStr(node["notice_type"]);
            Console.WriteLine("[NapCat] notice: " + notice);
            // Peer (or self-from-other-client) recalled a message.
            if (notice is "group_recall" or "friend_recall" or "recall")
                HandleRecallNotice(node);
        }
        else if (postType == "request")
        {
            // OneBot request event — keep flags for later approve/deny.
            var reqType = NapCatApiClient.ReadStr(node["request_type"]);
            var flag = NapCatApiClient.ReadStr(node["flag"]);
            var userId = NapCatApiClient.ReadLong(node["user_id"]);
            var groupId = NapCatApiClient.ReadLong(node["group_id"]);
            var comment = NapCatApiClient.ReadStr(node["comment"] ?? node["message"]);
            var subType = NapCatApiClient.ReadStr(node["sub_type"]);
            if (reqType == "friend" && !string.IsNullOrEmpty(flag))
            {
                if (userId > 0) _friendReqFlagByUin[userId.ToString()] = flag;
                lock (_gate)
                {
                    _friendRequests.RemoveAll(r => NapCatApiClient.ReadLong(r["uin"]) == userId);
                    _friendRequests.Add(new JsonObject
                    {
                        ["uin"] = userId,
                        ["name"] = userId.ToString(),
                        ["avatarPath"] = FriendAvatarUrl(userId),
                        ["message"] = comment,
                        ["handled"] = false,
                        ["flag"] = flag,
                    });
                }
                Console.WriteLine($"[NapCat] friend request uin={userId} flag={flag}");
            }
            else if (reqType == "group" && !string.IsNullOrEmpty(flag))
            {
                var seq = NapCatApiClient.ReadLong(node["seq"] ?? node["request_id"] ?? node["id"]);
                if (seq <= 0) seq = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var key = $"{groupId}:{seq}";
                _groupReqFlagByKey[key] = flag;
                if (!string.IsNullOrEmpty(flag)) _groupReqFlagByKey[flag] = flag;
                var notifType = subType is "invite" or "invite_me" ? "invite" : "join";
                lock (_gate)
                {
                    _groupNotifications.RemoveAll(n =>
                        NapCatApiClient.ReadLong(n["groupUin"]) == groupId
                        && NapCatApiClient.ReadLong(n["sequence"]) == seq);
                    _groupNotifications.Add(new JsonObject
                    {
                        ["groupUin"] = groupId,
                        ["sequence"] = seq,
                        ["type"] = notifType,
                        ["message"] = comment,
                        ["initiatorNickname"] = userId > 0 ? userId.ToString() : "成员",
                        ["initiatorUin"] = userId,
                        ["avatarPath"] = userId > 0 ? FriendAvatarUrl(userId) : GroupAvatarUrl(groupId),
                        ["isFiltered"] = false,
                        ["flag"] = flag,
                        ["subType"] = subType,
                    });
                }
                Console.WriteLine($"[NapCat] group request g={groupId} uin={userId} flag={flag}");
            }
        }
    }

    private JsonObject? MapIncomingMessage(JsonObject ev, bool isSentEcho, long? forcedPrivatePeer = null)
    {
        var messageType = NapCatApiClient.ReadStr(ev["message_type"]);
        var messageId = NapCatApiClient.ReadLong(ev["message_id"]);
        var userId = NapCatApiClient.ReadLong(ev["user_id"]);
        var groupId = NapCatApiClient.ReadLong(ev["group_id"]);
        var selfId = NapCatApiClient.ReadLong(ev["self_id"]);
        if (selfId > 0) _selfUin = selfId;

        string convId;
        string kind;
        if (messageType == "group" || groupId > 0)
        {
            convId = "g" + groupId;
            kind = "Group";
        }
        else
        {
            // private: peer is the other party.
            // Live self-echo: user_id may be self, target_id holds peer.
            // History self-sent rows often lack target_id — pass forcedPrivatePeer.
            long peer;
            if (forcedPrivatePeer is > 0)
            {
                peer = forcedPrivatePeer.Value;
            }
            else
            {
                peer = userId;
                if ((isSentEcho || peer == _selfUin) && peer == _selfUin)
                {
                    peer = NapCatApiClient.ReadLong(ev["target_id"]);
                    if (peer <= 0) peer = userId;
                }
            }
            if (peer <= 0) return null;
            convId = "f" + peer;
            kind = "Friend";
        }

        var sender = ev["sender"] as JsonObject;
        var senderUin = NapCatApiClient.ReadLong(sender?["user_id"]) is long su && su > 0 ? su : userId;
        var senderName = NapCatApiClient.ReadStr(sender?["card"]);
        if (string.IsNullOrEmpty(senderName)) senderName = NapCatApiClient.ReadStr(sender?["nickname"]);
        if (string.IsNullOrEmpty(senderName)) senderName = senderUin.ToString();

        var direction = isSentEcho || senderUin == _selfUin ? "Outgoing" : "Incoming";
        if (direction == "Outgoing")
        {
            senderUin = _selfUin;
            senderName = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname;
        }

        var (contentType, text, imagePath, elements) = MapSegments(ev["message"] ?? ev["raw_message"]);
        var location = ReadLocation(ev["message"] ?? ev["raw_message"]);
        var time = NapCatApiClient.ReadLong(ev["time"]);
        var dto = time > 0
            ? DateTimeOffset.FromUnixTimeSeconds(time)
            : DateTimeOffset.UtcNow;

        EnsureConversationRow(convId, kind, messageType == "group"
            ? NapCatApiClient.ReadStr(ev["group_name"])
            : senderName);

        // A few OneBot adapters omit message_id for synthetic/system events. A
        // shared :0 id would collapse every such event into one cached message.
        var id = messageId > 0
            ? $"{convId}:{messageId}"
            : $"{convId}:event-{Guid.NewGuid():N}";
        var wire = new JsonObject
        {
            ["id"] = id,
            ["conversationId"] = convId,
            ["senderName"] = senderName,
            ["senderUin"] = senderUin,
            ["senderAvatarPath"] = FriendAvatarUrl(senderUin),
            ["direction"] = direction,
            ["contentType"] = contentType,
            ["text"] = text,
            ["imagePath"] = imagePath,
            ["placeName"] = location.title,
            ["address"] = location.content,
            ["latitude"] = location.latitude,
            ["longitude"] = location.longitude,
            ["elements"] = elements,
            ["time"] = dto.ToString("o"),
            ["state"] = "Sent",
            ["napcatMessageId"] = messageId,
        };
        // Prefer group_name from the event when present; else conversation row title.
        var preferredTitle = messageType == "group"
            ? NapCatApiClient.ReadStr(ev["group_name"])
            : senderName;
        EnrichConversationMeta(wire, convId, preferredTitle);
        return wire;
    }

    private static (double latitude, double longitude, string title, string content) ReadLocation(JsonNode? message)
    {
        if (message is not JsonArray arr) return (0, 0, "", "");
        foreach (var segment in arr)
        {
            if (segment is not JsonObject so || !string.Equals(NapCatApiClient.ReadStr(so["type"]), "location", StringComparison.OrdinalIgnoreCase)) continue;
            var data = so["data"] as JsonObject;
            if (data == null) continue;
            var lat = NapCatApiClient.ReadDouble(data["lat"] ?? data["latitude"]);
            var lon = NapCatApiClient.ReadDouble(data["lon"] ?? data["longitude"]);
            return (lat, lon, NapCatApiClient.ReadStr(data["title"]), NapCatApiClient.ReadStr(data["content"]));
        }
        return (0, 0, "", "");
    }

    private static (string contentType, string text, string? imagePath, JsonArray elements) MapSegments(JsonNode? message)
    {
        var elements = new JsonArray();
        var textParts = new List<string>();
        string? firstImage = null;
        int imageCount = 0;

        void AddText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            textParts.Add(t);
            elements.Add(new JsonObject { ["Type"] = "Text", ["Text"] = t });
        }

        void AddImage(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            imageCount++;
            if (firstImage == null) firstImage = url;
            elements.Add(new JsonObject { ["Type"] = "Image", ["Url"] = url });
        }

        if (message is JsonValue jv && jv.TryGetValue<string>(out var plain) && plain != null)
        {
            AddText(plain);
        }
        else if (message is JsonArray arr)
        {
            foreach (var seg in arr)
            {
                if (seg is not JsonObject so) continue;
                var type = NapCatApiClient.ReadStr(so["type"]);
                var data = so["data"] as JsonObject;
                switch (type)
                {
                    case "text":
                        AddText(NapCatApiClient.ReadStr(data?["text"]));
                        break;
                    case "image":
                    case "mface":
                        AddImage(NapCatApiClient.ReadStr(data?["url"]).Length > 0
                            ? NapCatApiClient.ReadStr(data?["url"])
                            : NapCatApiClient.ReadStr(data?["file"]));
                        break;
                    case "at":
                        var qq = NapCatApiClient.ReadStr(data?["qq"]);
                        var name = NapCatApiClient.ReadStr(data?["name"]);
                        var atText = string.IsNullOrEmpty(name) ? "@" + qq : "@" + name;
                        elements.Add(new JsonObject { ["Type"] = "Mention", ["Text"] = atText, ["Uin"] = NapCatApiClient.ReadLong(data?["qq"]) });
                        textParts.Add(atText);
                        break;
                    case "reply":
                        // quote handled separately if needed
                        break;
                    case "record":
                        elements.Add(new JsonObject { ["Type"] = "Record", ["Url"] = NapCatApiClient.ReadStr(data?["url"] ?? data?["file"]) });
                        textParts.Add("[语音]");
                        break;
                    case "face":
                        var faceId = NapCatApiClient.ReadStr(data?["id"]);
                        elements.Add(new JsonObject { ["Type"] = "Face", ["Text"] = faceId, ["Url"] = faceId });
                        textParts.Add("[表情]");
                        break;
                    case "dice":
                        elements.Add(new JsonObject { ["Type"] = "Dice", ["Text"] = NapCatApiClient.ReadStr(data?["result"]) });
                        textParts.Add("[骰子" + NapCatApiClient.ReadStr(data?["result"]) + "]");
                        break;
                    case "rps":
                        elements.Add(new JsonObject { ["Type"] = "Rps", ["Text"] = NapCatApiClient.ReadStr(data?["result"]) });
                        textParts.Add("[猜拳]");
                        break;
                    case "json":
                    case "xml":
                        elements.Add(new JsonObject
                        {
                            ["Type"] = "Card",
                            ["Text"] = NapCatApiClient.ReadStr(data?["data"] ?? data?["text"] ?? data?["content"]),
                        });
                        textParts.Add("[卡片消息]");
                        break;
                    case "forward":
                        elements.Add(new JsonObject { ["Type"] = "Forward", ["Url"] = NapCatApiClient.ReadStr(data?["id"]), ["Text"] = "合并转发" });
                        textParts.Add("[合并转发]");
                        break;
                    case "location":
                        elements.Add(new JsonObject
                        {
                            ["Type"] = "Location",
                            ["Text"] = NapCatApiClient.ReadStr(data?["content"] ?? data?["title"]),
                        });
                        textParts.Add(NapCatApiClient.ReadStr(data?["title"] ?? data?["content"]));
                        break;
                    case "video":
                        elements.Add(new JsonObject { ["Type"] = "Video", ["Url"] = NapCatApiClient.ReadStr(data?["url"]) });
                        textParts.Add("[视频]");
                        break;
                    case "file":
                        elements.Add(new JsonObject { ["Type"] = "File", ["Text"] = NapCatApiClient.ReadStr(data?["name"]) });
                        textParts.Add("[文件]");
                        break;
                    default:
                        var raw = NapCatApiClient.ReadStr(data?["text"]);
                        if (!string.IsNullOrEmpty(raw)) AddText(raw);
                        break;
                }
            }
        }

        var text = string.Join("", textParts);
        if (string.IsNullOrEmpty(text) && imageCount > 0)
            text = imageCount > 1 ? $"[图片×{imageCount}]" : "[图片]";

        string contentType;
        if (imageCount > 0 && textParts.Count > 0) contentType = "Mixed";
        else if (imageCount > 0) contentType = "Image";
        else contentType = "Text";

        foreach (var element in elements)
        {
            if (element is JsonObject eo && string.Equals(NapCatApiClient.ReadStr(eo["Type"]), "Forward", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "Forward";
                break;
            }
            if (element is JsonObject location && string.Equals(NapCatApiClient.ReadStr(location["Type"]), "Location", StringComparison.OrdinalIgnoreCase))
                contentType = "Location";
        }

        return (contentType, text, firstImage, elements);
    }

    private async Task PopulateListsAsync()
    {
        // Friends
        var (friendsData, fErr) = await _api.CallAsync("get_friend_list");
        if (fErr != null) Console.WriteLine("[NapCat] get_friend_list: " + fErr);
        var friendArr = friendsData as JsonArray ?? friendsData?["friends"] as JsonArray ?? friendsData?["data"] as JsonArray;

        // Groups
        var (groupsData, gErr) = await _api.CallAsync("get_group_list");
        if (gErr != null) Console.WriteLine("[NapCat] get_group_list: " + gErr);
        var groupArr = groupsData as JsonArray ?? groupsData?["groups"] as JsonArray;

        lock (_gate)
        {
            _contacts.Clear();
            _conversations.Clear();
            if (friendArr != null)
            {
                foreach (var n in friendArr)
                {
                    if (n is not JsonObject o) continue;
                    var uin = NapCatApiClient.ReadLong(o["user_id"] ?? o["uin"]);
                    if (uin <= 0) continue;
                    var remark = NapCatApiClient.ReadStr(o["remark"]);
                    var nickname = NapCatApiClient.ReadStr(o["nickname"] ?? o["nick"]);
                    var display = PreferDisplayName(card: "", remark, nickname, qid: "", uin);
                    var avatar = FriendAvatarUrl(uin);
                    _contacts.Add(new JsonObject
                    {
                        ["uin"] = uin,
                        ["name"] = string.IsNullOrEmpty(nickname) ? display : nickname,
                        ["remark"] = remark,
                        ["avatarPath"] = avatar,
                        ["signature"] = NapCatApiClient.ReadStr(o["longNick"] ?? o["long_nick"] ?? o["signature"]),
                        ["online"] = false,
                    });
                    var convId = "f" + uin;
                    var row = new JsonObject
                    {
                        ["id"] = convId,
                        ["kind"] = "Friend",
                        ["title"] = display,
                        ["avatarPath"] = avatar,
                        ["preview"] = "",
                        ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["unread"] = 0,
                    };
                    ApplyPrefsTo(row);
                    _conversations.Add(row);
                }
            }
            if (groupArr != null)
            {
                foreach (var n in groupArr)
                {
                    if (n is not JsonObject o) continue;
                    var gin = NapCatApiClient.ReadLong(o["group_id"] ?? o["uin"]);
                    if (gin <= 0) continue;
                    var name = NapCatApiClient.ReadStr(o["group_name"] ?? o["name"]);
                    if (string.IsNullOrEmpty(name)) name = gin.ToString();
                    var convId = "g" + gin;
                    var row = new JsonObject
                    {
                        ["id"] = convId,
                        ["kind"] = "Group",
                        ["title"] = name,
                        ["avatarPath"] = GroupAvatarUrl(gin),
                        ["preview"] = "",
                        ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["unread"] = 0,
                        ["announcement"] = "",
                    };
                    ApplyPrefsTo(row);
                    _conversations.Add(row);
                }
            }
        }

        // Fill announcements outside the lock (async HTTP; cap to avoid startup storms).
        List<(string convId, long gin)> groups;
        lock (_gate)
        {
            groups = _conversations
                .Where(c => (string?)c["kind"] == "Group")
                .Select(c =>
                {
                    var id = (string?)c["id"] ?? "";
                    long.TryParse(id.Length > 1 ? id.AsSpan(1) : default, out var g);
                    return (id, g);
                })
                .Where(t => t.g > 0)
                .Take(30)
                .ToList();
        }
        foreach (var (convId, gin) in groups)
        {
            try
            {
                var (gInfo, _) = await _api.CallAsync("get_group_info", new JsonObject { ["group_id"] = gin });
                var memo = NapCatApiClient.ReadStr(gInfo?["group_memo"] ?? gInfo?["group_notice"] ?? gInfo?["finger_memo"]);
                if (string.IsNullOrEmpty(memo)) continue;
                lock (_gate)
                {
                    var row = _conversations.FirstOrDefault(c => (string?)c["id"] == convId);
                    if (row != null) row["announcement"] = memo;
                }
            }
            catch { /* ignore */ }
        }

        Console.WriteLine($"[NapCat] populated contacts={_contacts.Count} conversations={_conversations.Count}");
    
        try
        {
            var (recentData, rErr) = await _api.CallAsync("get_recent_contact", new JsonObject { ["count"] = 50 });
            if (rErr != null) Console.WriteLine("[NapCat] get_recent_contact: " + rErr);
            else ApplyRecentContacts(recentData);
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] recent hydrate: " + ex.Message); }

    }

    private void EnsureConversationRow(string convId, string kind, string? title)
    {
        lock (_gate)
        {
            long uin = 0;
            if (convId.Length > 1) long.TryParse(convId.AsSpan(1), out uin);

            var existing = _conversations.FirstOrDefault(c => (string?)c["id"] == convId);
            if (existing != null)
            {
                // Upgrade placeholder titles (convId / bare uin) when NapCat later
                // supplies a real group_name or peer nickname.
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var cur = (string?)existing["title"] ?? "";
                    if (string.IsNullOrWhiteSpace(cur)
                        || cur == convId
                        || (uin > 0 && cur == uin.ToString()))
                        existing["title"] = title.Trim();
                }
                if (string.IsNullOrWhiteSpace((string?)existing["avatarPath"]) && uin > 0)
                    existing["avatarPath"] = kind == "Group" ? GroupAvatarUrl(uin) : FriendAvatarUrl(uin);
                return;
            }

            var row = new JsonObject
            {
                ["id"] = convId,
                ["kind"] = kind,
                ["title"] = string.IsNullOrEmpty(title) ? convId : title,
                ["avatarPath"] = kind == "Group" ? GroupAvatarUrl(uin) : FriendAvatarUrl(uin),
                ["preview"] = "",
                ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                ["unread"] = 0,
            };
            ApplyPrefsTo(row);
            _conversations.Add(row);
        }
    }

    private void BumpConversation(string convId, string? preview, bool incrementUnread, DateTimeOffset? messageTime = null)
    {
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == convId);
            if (conv == null) return;
            if (!string.IsNullOrEmpty(preview)) conv["preview"] = preview;
            conv["lastTime"] = DateTimeOffset.UtcNow.ToString("o");
            if (!_convPrefs.TryGetValue(convId, out var pref) || pref == null)
            {
                pref = new ConvPref();
                _convPrefs[convId] = pref;
            }
            var countsAsUnread = incrementUnread && !pref.Muted;
            if (countsAsUnread && messageTime.HasValue
                && !string.IsNullOrEmpty(pref.LastReadAt)
                && DateTimeOffset.TryParse(pref.LastReadAt, out var lastReadAt)
                && messageTime.Value <= lastReadAt)
            {
                // Event WS delivery can lag a read acknowledgement. Do not resurrect
                // an unread badge for a message that was already read by its timestamp.
                countsAsUnread = false;
            }
            if (countsAsUnread)
            {
                var u = Math.Max(pref.Unread, (int)NapCatApiClient.ReadLong(conv["unread"])) + 1;
                conv["unread"] = u;
                pref.Unread = u;
                SavePrefs();
            }
        }
    }

    private void BroadcastLoginStatus(string state, long uin, string? message)
    {
        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "loginStatus",
            ["data"] = new JsonObject
            {
                ["state"] = state,
                ["uin"] = uin,
                ["message"] = message ?? "",
                ["backend"] = BackendId,
            },
        }.ToJsonString());
    }

}
