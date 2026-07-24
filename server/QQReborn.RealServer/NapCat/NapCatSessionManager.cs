using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

/// <summary>
/// OneBot 11 / NapCat-backed session that speaks the App wire protocol.
/// Login is owned by NapCat/NTQQ; configureAccount only verifies connectivity
/// and starts the event stream.
/// </summary>
public sealed class NapCatSessionManager : ISessionBackend, IAsyncDisposable
{
    private readonly NapCatOptions _opts;
    private readonly NapCatApiClient _api;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _messages = new();
    private readonly List<JsonObject> _conversations = new();
    private readonly List<JsonObject> _contacts = new();
    private readonly Dictionary<string, (bool pinned, bool muted)> _convPrefs = new();
    private readonly ConcurrentDictionary<string, JsonObject> _msgIndex = new();

    private long _selfUin;
    private string _selfNickname = "";
    private bool _online;
    private CancellationTokenSource? _wsCts;
    private Task? _wsLoop;
    private long _prefsUin;

    // Pending system requests (flag required by NapCat set_*_add_request).
    private readonly List<JsonObject> _friendRequests = new();
    private readonly List<JsonObject> _groupNotifications = new();
    private readonly Dictionary<string, string> _friendReqFlagByUin = new();
    private readonly Dictionary<string, string> _groupReqFlagByKey = new();
    private QzoneFeedClient? _qzone;

    public string BackendId => BackendFactory.NapCat;
    public event Action<string>? Broadcast;

    public NapCatSessionManager(NapCatOptions opts)
    {
        _opts = opts;
        _api = new NapCatApiClient(opts);
        Console.WriteLine($"[NapCat] HTTP={opts.HttpBase}  WS={opts.EventWs}");
    }

    public async ValueTask DisposeAsync()
    {
        _wsCts?.Cancel();
        if (_wsLoop != null)
        {
            try { await _wsLoop; } catch { /* ignore */ }
        }
        _api.Dispose();
    }

    // ---- account ----

    public async Task<(JsonObject? data, string? error)> ConfigureAccountAsync(string signUrl, string? signToken, string signUinRaw)
    {
        // signUrl/signToken are Lagrange-only; NapCat ignores them.
        // Optional: signUin must match NapCat logged-in account when provided.
        try
        {
            var (data, err) = await _api.CallAsync("get_login_info");
            if (err != null) return (null, "无法连接 NapCat（" + err + "）。请确认 NTQQ+NapCat 已登录且 HTTP API 已开。");
            if (data == null) return (null, "get_login_info 无 data");

            var uin = NapCatApiClient.ReadLong(data["user_id"] ?? data["uin"]);
            var nick = NapCatApiClient.ReadStr(data["nickname"]);
            if (uin <= 0) return (null, "NapCat 未返回有效 QQ 号");

            // localGateway: Shell may leave QQ empty — adopt NapCat's current account.
            // If Shell sent a number, it must match (prevents binding the wrong session).
            if (!string.IsNullOrWhiteSpace(signUinRaw)
                && long.TryParse(signUinRaw.Trim(), out var expect)
                && expect > 0
                && expect != uin)
            {
                return (null, $"NapCat 当前登录 {uin}，与 App 填写的 {expect} 不一致。本机网关模式可清空 QQ 号再试。");
            }

            lock (_gate)
            {
                _selfUin = uin;
                _selfNickname = nick;
                _online = true;
                _messages.Clear();
                _conversations.Clear();
                _contacts.Clear();
                _msgIndex.Clear();
                LoadPrefs(uin);
            }

            await PopulateListsAsync();
            EnsureEventLoop();
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
            BroadcastLoginStatus("online", uin, nick);
            Console.WriteLine($"[NapCat] gateway online uin={uin} nick={nick}");
            return (new JsonObject
            {
                ["accepted"] = true,
                ["backend"] = BackendId,
                ["mode"] = "localGateway",
                ["uin"] = uin,
                ["nickname"] = nick,
                ["hint"] = "本机网关：出门请用 SakuraFrp 映射 8765，见 docs/USER-GATEWAY-SAKURAFRP.md",
            }, null);
        }
        catch (Exception ex)
        {
            return (null, "configureAccount failed: " + ex.Message);
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
                list.Add(wire);
                _msgIndex[id] = wire;
                BumpConversation(convId, (string?)wire["text"], (string?)wire["direction"] == "Incoming");
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

    private JsonObject? MapIncomingMessage(JsonObject ev, bool isSentEcho)
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
            // private: peer is user_id (for self-echo of outbound, still the peer)
            var peer = userId;
            if (isSentEcho && peer == _selfUin)
            {
                // some stacks put target in target_id
                peer = NapCatApiClient.ReadLong(ev["target_id"]);
                if (peer <= 0) peer = userId;
            }
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
        var time = NapCatApiClient.ReadLong(ev["time"]);
        var dto = time > 0
            ? DateTimeOffset.FromUnixTimeSeconds(time)
            : DateTimeOffset.UtcNow;

        EnsureConversationRow(convId, kind, messageType == "group"
            ? NapCatApiClient.ReadStr(ev["group_name"])
            : senderName);

        var id = $"{convId}:{messageId}";
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
            ["elements"] = elements,
            ["time"] = dto.ToString("o"),
            ["state"] = "Sent",
            ["napcatMessageId"] = messageId,
        };
        return wire;
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
                        elements.Add(new JsonObject { ["Type"] = "Record", ["Url"] = NapCatApiClient.ReadStr(data?["url"]) });
                        textParts.Add("[语音]");
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
                    var name = PreferDisplayName(card: "", remark, nickname, qid: "", uin);
                    var avatar = FriendAvatarUrl(uin);
                    _contacts.Add(new JsonObject
                    {
                        ["uin"] = uin,
                        ["name"] = name,
                        ["avatarPath"] = avatar,
                        ["signature"] = NapCatApiClient.ReadStr(o["longNick"] ?? o["long_nick"] ?? o["signature"]),
                        ["online"] = false,
                    });
                    var convId = "f" + uin;
                    var row = new JsonObject
                    {
                        ["id"] = convId,
                        ["kind"] = "Friend",
                        ["title"] = name,
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
    }

    private void EnsureConversationRow(string convId, string kind, string? title)
    {
        lock (_gate)
        {
            if (_conversations.Any(c => (string?)c["id"] == convId)) return;
            long uin = 0;
            if (convId.Length > 1) long.TryParse(convId.AsSpan(1), out uin);
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

    private void BumpConversation(string convId, string? preview, bool incrementUnread)
    {
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == convId);
            if (conv == null) return;
            if (!string.IsNullOrEmpty(preview)) conv["preview"] = preview;
            conv["lastTime"] = DateTimeOffset.UtcNow.ToString("o");
            var muted = _convPrefs.TryGetValue(convId, out var p) && p.muted;
            if (incrementUnread && !muted)
            {
                var u = NapCatApiClient.ReadLong(conv["unread"]);
                conv["unread"] = u + 1;
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
                ["signature"] = "",
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

    private async Task TryPullHistoryAsync(string conversationId, int count, string? beforeId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer)) return;
        count = count <= 0 ? 20 : Math.Min(count, 50);

        // NapCat extensions (names vary slightly across versions)
        string action;
        var parameters = new JsonObject { ["count"] = count };
        if (kind == 'g')
        {
            action = "get_group_msg_history";
            parameters["group_id"] = peer;
            if (!string.IsNullOrEmpty(beforeId) && beforeId.Contains(':')
                && long.TryParse(beforeId.Split(':').Last(), out var mid))
                parameters["message_seq"] = mid; // some builds use message_id / message_seq
        }
        else
        {
            action = "get_friend_msg_history";
            parameters["user_id"] = peer;
            // alternate action names tried on failure
        }

        var (data, err) = await _api.CallAsync(action, parameters);
        if (err != null && kind == 'f')
        {
            (data, err) = await _api.CallAsync("get_friend_msg_history", parameters);
            if (err != null)
                (data, err) = await _api.CallAsync("get_msg_history", parameters);
        }
        if (err != null)
        {
            Console.WriteLine($"[NapCat] history {action}: {err}");
            return;
        }

        var messages = data as JsonArray
            ?? data?["messages"] as JsonArray
            ?? data?["message"] as JsonArray;
        if (messages == null) return;

        foreach (var n in messages)
        {
            if (n is not JsonObject o) continue;
            // Normalize to event-like object
            if (o["message_type"] == null)
                o["message_type"] = kind == 'g' ? "group" : "private";
            if (kind == 'g' && o["group_id"] == null) o["group_id"] = peer;
            if (kind == 'f' && o["user_id"] == null) o["user_id"] = peer;
            var wire = MapIncomingMessage(o, false);
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
                list.Insert(0, wire); // history usually oldest-first or newest — keep insertion then sort
                _msgIndex[id] = wire;
            }
        }

        lock (_gate)
        {
            if (_messages.TryGetValue(conversationId, out var list))
            {
                var ordered = list
                    .OrderBy(m => (string?)m["time"] ?? "")
                    .ToList();
                _messages[conversationId] = ordered;
            }
        }
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
        await TryPullHistoryAsync(conversationId, count > 0 ? count : 20, beforeId);
        JsonArray messages;
        lock (_gate)
        {
            messages = new JsonArray();
            if (_messages.TryGetValue(conversationId, out var list))
            {
                IEnumerable<JsonObject> q = list;
                if (!string.IsNullOrEmpty(beforeId))
                {
                    var idx = list.FindIndex(m => (string?)m["id"] == beforeId);
                    if (idx > 0) q = list.Take(idx);
                    else if (idx == 0) q = Array.Empty<JsonObject>();
                }
                var take = count > 0 ? count : 20;
                foreach (var m in q.Reverse().Take(take).Reverse())
                    messages.Add(Clone(m));
            }
        }
        var hasMore = messages.Count >= (count > 0 ? count : 20);
        return (new JsonObject { ["messages"] = messages, ["hasMore"] = hasMore }, null);
    }

    public async Task<JsonObject> RecallMessageAsync(string conversationId, string messageId)
    {
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return new JsonObject { ["ok"] = false, ["reason"] = "invalid-message-id" };
        var (data, err) = await _api.CallAsync("delete_msg", new JsonObject { ["message_id"] = mid });
        if (err != null) return new JsonObject { ["ok"] = false, ["reason"] = err };
        return new JsonObject { ["ok"] = true, ["data"] = data?.DeepClone() };
    }

    public async Task<JsonObject> QuitGroupAsync(string conversationId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer) || kind != 'g')
            return new JsonObject { ["ok"] = false, ["reason"] = "not-a-group" };
        var (_, err) = await _api.CallAsync("set_group_leave", new JsonObject { ["group_id"] = peer });
        if (err != null) return new JsonObject { ["ok"] = false, ["reason"] = err };
        lock (_gate) _conversations.RemoveAll(c => (string?)c["id"] == conversationId);
        return new JsonObject { ["ok"] = true };
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
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length > 0)
                {
                    return (new JsonObject
                    {
                        ["audioBase64"] = Convert.ToBase64String(bytes),
                        ["format"] = "bin",
                        ["duration"] = 0,
                        ["url"] = url,
                    }, null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[NapCat] voice download: " + ex.Message);
            }
            return (new JsonObject { ["url"] = url, ["format"] = "url", ["duration"] = 0 }, null);
        }

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
            return (new JsonObject { ["url"] = string.IsNullOrEmpty(local) ? url : local, ["format"] = "path" }, null);
        }
        catch (Exception ex)
        {
            return (new JsonObject { ["url"] = url, ["format"] = "path" }, "voice: " + ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> GetFileDownloadUrlAsync(string conversationId, string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return (null, "empty-file-id");
        // Already a URL?
        if (fileId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (new JsonObject { ["url"] = fileId }, null);

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
        return _qzone.Snapshot();
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

    public async Task<(JsonObject? data, string? error)> SendAsync(
        string conversationId, string text, string? replyToId = null,
        string contentType = "Text", string? placeName = null, string? address = null, string? thumb = null,
        string? imageBase64 = null, JsonNode? imagesBase64Node = null, string? audioBase64 = null, int voiceSeconds = 0,
        string? fileBase64 = null, string? fileName = null, string? mentionsJson = null)
    {
        if (!_online) return (null, "not-online");
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");

        // File: prefer dedicated upload APIs (NapCat), then fall back to message segment.
        if (contentType == "File" && !string.IsNullOrEmpty(fileBase64))
            return await SendFileAsync(conversationId, kind, peer, fileBase64, fileName);

        var segments = new JsonArray();
        if (!string.IsNullOrEmpty(replyToId))
        {
            var mid = ExtractNapCatMessageId(replyToId);
            if (mid > 0)
                segments.Add(new JsonObject { ["type"] = "reply", ["data"] = new JsonObject { ["id"] = mid.ToString() } });
        }

        // Mentions: Shell sends JSON array [{ "uin": 123, "display": "@昵称" }, ...]
        var mentionParts = ParseMentions(mentionsJson);
        foreach (var m in mentionParts)
        {
            var qq = m.uin > 0 ? m.uin.ToString() : "all";
            var atName = (m.display ?? "").TrimStart('@');
            if (string.IsNullOrEmpty(atName)) atName = m.uin > 0 ? m.uin.ToString() : "全体成员";
            segments.Add(new JsonObject
            {
                ["type"] = "at",
                ["data"] = new JsonObject { ["qq"] = qq, ["name"] = atName },
            });
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = " " } });
        }

        // Caption text (strip pure display tokens already covered by at segments when possible)
        var caption = text ?? "";
        if (mentionParts.Count > 0 && !string.IsNullOrWhiteSpace(caption))
        {
            foreach (var m in mentionParts)
            {
                if (string.IsNullOrEmpty(m.display)) continue;
                caption = caption.Replace(m.display, "", StringComparison.Ordinal);
            }
            caption = caption.Trim();
        }

        if (contentType is "Text" or "Mixed" or "" || (contentType is "Image" or "Sticker" && !string.IsNullOrWhiteSpace(caption)))
        {
            if (!string.IsNullOrWhiteSpace(caption) && contentType != "Location")
                segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = caption } });
        }
        if (contentType == "Location")
        {
            var loc = string.IsNullOrEmpty(address) ? $"[位置] {placeName}" : $"[位置] {placeName}（{address}）";
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = loc } });
        }

        // Images (图文混排: text/at segments above + image segments).
        // Prefer a real temp file path for NapCat — large base64:// payloads often
        // return message_id but render as empty / broken thumbnails on QQ clients.
        var images = CollectImages(imageBase64, imagesBase64Node);
        var tempImagePaths = new List<string>();
        foreach (var b64 in images)
        {
            var path = TryWriteTempMedia(b64, ".jpg");
            if (path != null) tempImagePaths.Add(path);
            var fileRef = path ?? ("base64://" + StripDataUrl(b64));
            segments.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = new JsonObject { ["file"] = fileRef },
            });
        }

        if (contentType == "Voice" && !string.IsNullOrEmpty(audioBase64))
        {
            segments.Add(new JsonObject
            {
                ["type"] = "record",
                ["data"] = new JsonObject { ["file"] = "base64://" + audioBase64 },
            });
        }

        if (segments.Count == 0)
            return (null, "empty-message");

        string action;
        var parameters = new JsonObject { ["message"] = segments };
        if (kind == 'g')
        {
            action = "send_group_msg";
            parameters["group_id"] = peer;
        }
        else
        {
            action = "send_private_msg";
            parameters["user_id"] = peer;
        }

        JsonNode? data;
        string? err;
        try
        {
            (data, err) = await _api.CallAsync(action, parameters);
            if (err != null) return (null, err);

            var messageId = NapCatApiClient.ReadLong(data?["message_id"]);
            var id = $"{conversationId}:{messageId}";

            // message_sent event often arrives with real CDN URLs slightly before/after this.
            // Prefer that echo so Shell gets http imagePath (not empty / base64 / temp path).
            if (messageId > 0 && images.Count > 0)
            {
                for (var wait = 0; wait < 20; wait++)
                {
                    lock (_gate)
                    {
                        if (_msgIndex.TryGetValue(id, out var echoed))
                        {
                            var echoPath = NapCatApiClient.ReadStr(echoed["imagePath"]);
                            if (echoPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                return (Clone(echoed), null);
                        }
                    }
                    await Task.Delay(50);
                }
            }

            var (wireType, wireText, imagePath, elements) = MapSegments(segments);
            // Never echo base64:// or local temp paths to Shell — Image control can't load them
            // (shows empty). Prefer CDN from NapCat; else empty so client can patch local path.
            imagePath = ExtractUrlFromSendResult(data) ?? SanitizeOutboundImagePath(imagePath);
            if (contentType is "Image" or "Sticker" or "Mixed" && images.Count > 0 && string.IsNullOrEmpty(imagePath))
            {
                wireType = (!string.IsNullOrWhiteSpace(text) || mentionParts.Count > 0) && images.Count > 0 ? "Mixed"
                    : images.Count > 1 ? "Mixed" : "Image";
                if (string.IsNullOrEmpty(wireText))
                    wireText = images.Count > 1 ? $"[图片×{images.Count}]" : "[图片]";
            }
            if (mentionParts.Count > 0 && string.IsNullOrEmpty(wireText))
                wireText = string.Join(" ", mentionParts.Select(m => string.IsNullOrEmpty(m.display) ? "@" + m.uin : m.display));

            ScrubNonHttpImageUrls(elements, imagePath);

            var wire = new JsonObject
            {
                ["id"] = id,
                ["conversationId"] = conversationId,
                ["senderName"] = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname,
                ["senderUin"] = _selfUin,
                ["senderAvatarPath"] = FriendAvatarUrl(_selfUin),
                ["direction"] = "Outgoing",
                ["contentType"] = wireType,
                ["text"] = wireText,
                ["imagePath"] = imagePath,
                ["elements"] = elements,
                ["time"] = DateTimeOffset.UtcNow.ToString("o"),
                ["state"] = "Sent",
                ["napcatMessageId"] = messageId,
            };

            lock (_gate)
            {
                // Event may have already inserted the same id — don't duplicate.
                if (_msgIndex.TryGetValue(id, out var existing))
                    return (Clone(existing), null);
                if (!_messages.TryGetValue(conversationId, out var list))
                {
                    list = new List<JsonObject>();
                    _messages[conversationId] = list;
                }
                list.Add(wire);
                _msgIndex[id] = wire;
                BumpConversation(conversationId, wireText, incrementUnread: false);
            }

            return (Clone(wire), null);
        }
        finally
        {
            foreach (var p in tempImagePaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
        }
    }

    private static void ScrubNonHttpImageUrls(JsonArray? elements, string? httpFallback)
    {
        if (elements == null) return;
        foreach (var el in elements)
        {
            if (el is not JsonObject eo) continue;
            var t = NapCatApiClient.ReadStr(eo["Type"] ?? eo["type"]);
            if (!string.Equals(t, "Image", StringComparison.OrdinalIgnoreCase)) continue;
            var u = NapCatApiClient.ReadStr(eo["Url"] ?? eo["url"]);
            if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(httpFallback) && httpFallback.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                eo["Url"] = httpFallback;
            else
            {
                eo.Remove("Url");
                eo.Remove("url");
            }
        }
    }

    private static string StripDataUrl(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return b64;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            return b64[(comma + 1)..];
        return b64;
    }

    private static string? TryWriteTempMedia(string b64, string ext)
    {
        try
        {
            var raw = StripDataUrl(b64);
            var bytes = Convert.FromBase64String(raw);
            if (bytes.Length == 0) return null;
            // sniff real extension
            if (bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50) ext = ".png";
            else if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) ext = ".jpg";
            else if (bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49) ext = ".gif";
            else if (bytes.Length > 12 && bytes[0] == 0x52 && bytes[8] == 0x57) ext = ".webp";

            var dir = Path.Combine(Path.GetTempPath(), "QQReborn", "outbox");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NapCat] temp media write failed: " + ex.Message);
            return null;
        }
    }

    private static string? ExtractUrlFromSendResult(JsonNode? data)
    {
        if (data == null) return null;
        // Some NapCat builds return url / file in send result
        foreach (var key in new[] { "url", "file", "image_url", "file_url" })
        {
            var s = NapCatApiClient.ReadStr(data[key]);
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    private static string? SanitizeOutboundImagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
        // base64:// or local temp path is useless/broken in Shell Image control
        return null;
    }

    private async Task<(JsonObject? data, string? error)> SendFileAsync(
        string conversationId, char kind, long peer, string fileBase64, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName.Trim();
        // Strip data-url prefix if Shell ever sends it.
        var b64 = fileBase64;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            b64 = b64[(comma + 1)..];

        // 1) Prefer OneBot file segment in chat (returns message_id, shows in conversation)
        var segments = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "file",
                ["data"] = new JsonObject
                {
                    ["file"] = "base64://" + b64,
                    ["name"] = name,
                },
            },
        };
        string action = kind == 'g' ? "send_group_msg" : "send_private_msg";
        var parameters = new JsonObject { ["message"] = segments };
        if (kind == 'g') parameters["group_id"] = peer;
        else parameters["user_id"] = peer;

        var (data, err) = await _api.CallAsync(action, parameters);
        if (err == null)
        {
            var messageId = NapCatApiClient.ReadLong(data?["message_id"]);
            var wire = BuildOutgoingWire(
                conversationId,
                contentType: "File",
                text: $"[文件] {name}",
                imagePath: null,
                elements: new JsonArray { new JsonObject { ["Type"] = "File", ["Text"] = name } },
                messageId: messageId,
                fileId: null);
            return (wire, null);
        }
        Console.WriteLine($"[NapCat] file segment: {err}; try upload_*_file");

        // 2) NapCat offline-file upload APIs
        string uploadAction = kind == 'g' ? "upload_group_file" : "upload_private_file";
        var uploadParams = new JsonObject
        {
            ["file"] = "base64://" + b64,
            ["name"] = name,
        };
        if (kind == 'g') uploadParams["group_id"] = peer;
        else uploadParams["user_id"] = peer;

        var (upData, upErr) = await _api.CallAsync(uploadAction, uploadParams);
        if (upErr != null)
            return (null, $"file-send-failed: segment={err}; upload={upErr}");

        var fileId = NapCatApiClient.ReadStr(upData?["file_id"] ?? upData?["id"]);
        var mid = NapCatApiClient.ReadLong(upData?["message_id"]);
        var wireOk = BuildOutgoingWire(
            conversationId,
            contentType: "File",
            text: $"[文件] {name}",
            imagePath: null,
            elements: new JsonArray
            {
                new JsonObject { ["Type"] = "File", ["Text"] = name, ["FileId"] = fileId },
            },
            messageId: mid,
            fileId: fileId);
        return (wireOk, null);
    }

    private JsonObject BuildOutgoingWire(
        string conversationId, string contentType, string text, string? imagePath, JsonArray elements,
        long messageId, string? fileId = null)
    {
        var id = messageId > 0
            ? $"{conversationId}:{messageId}"
            : !string.IsNullOrEmpty(fileId)
                ? $"{conversationId}:file:{fileId}"
                : $"{conversationId}:local-{Guid.NewGuid():N}";
        var wire = new JsonObject
        {
            ["id"] = id,
            ["conversationId"] = conversationId,
            ["senderName"] = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname,
            ["senderUin"] = _selfUin,
            ["senderAvatarPath"] = FriendAvatarUrl(_selfUin),
            ["direction"] = "Outgoing",
            ["contentType"] = contentType,
            ["text"] = text,
            ["imagePath"] = imagePath,
            ["elements"] = elements,
            ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            ["state"] = "Sent",
            ["napcatMessageId"] = messageId,
        };
        if (!string.IsNullOrEmpty(fileId)) wire["fileId"] = fileId;
        lock (_gate)
        {
            if (!_messages.TryGetValue(conversationId, out var list))
            {
                list = new List<JsonObject>();
                _messages[conversationId] = list;
            }
            list.Add(wire);
            _msgIndex[id] = wire;
            BumpConversation(conversationId, text, incrementUnread: false);
        }
        return Clone(wire);
    }

    private static List<(long uin, string display)> ParseMentions(string? mentionsJson)
    {
        var list = new List<(long uin, string display)>();
        if (string.IsNullOrWhiteSpace(mentionsJson)) return list;
        try
        {
            var node = JsonNode.Parse(mentionsJson);
            if (node is not JsonArray arr) return list;
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var uin = NapCatApiClient.ReadLong(o["uin"] ?? o["qq"] ?? o["user_id"]);
                var display = NapCatApiClient.ReadStr(o["display"] ?? o["name"] ?? o["text"]);
                var isAll = uin <= 0 && (
                    string.Equals(display, "@all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, "@全体成员", StringComparison.Ordinal)
                    || string.Equals(display, "全体成员", StringComparison.Ordinal)
                    || (display != null && display.IndexOf("全体", StringComparison.Ordinal) >= 0));
                if (uin <= 0 && !isAll)
                    continue;
                if (isAll)
                {
                    uin = 0;
                    if (string.IsNullOrEmpty(display)) display = "@全体成员";
                }
                if (string.IsNullOrEmpty(display) && uin > 0) display = "@" + uin;
                list.Add((uin, display ?? ""));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NapCat] parse mentions: " + ex.Message);
        }
        return list;
    }

    public async Task<(JsonObject? data, string? error)> ForwardAsync(string conversationId, string messageId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return (null, "invalid-message-id");

        // Resolve source bubble (for caption + custom-node fallback).
        JsonObject? src = null;
        if (!_msgIndex.TryGetValue(messageId, out src))
        {
            foreach (var kv in _msgIndex)
            {
                if (kv.Key.EndsWith(":" + mid, StringComparison.Ordinal)
                    || NapCatApiClient.ReadLong(kv.Value["napcatMessageId"]) == mid)
                {
                    src = kv.Value;
                    break;
                }
            }
        }

        // Prefer go-cq multi-forward (returns real message_id). forward_*_single_msg
        // returns data:null even when NTQQ silently drops the forward — not trustworthy.
        var idNode = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "node",
                ["data"] = new JsonObject { ["id"] = mid.ToString() },
            },
        };
        var (data, err) = await CallForwardMsgAsync(kind, peer, idNode);
        var newMid = NapCatApiClient.ReadLong(data?["message_id"]);

        if (newMid <= 0)
        {
            // Custom node with source text — still produces a real merged-forward card.
            var srcText = NapCatApiClient.ReadStr(src?["text"]);
            if (string.IsNullOrWhiteSpace(srcText)) srcText = "[转发消息]";
            var senderName = NapCatApiClient.ReadStr(src?["senderName"]);
            if (string.IsNullOrWhiteSpace(senderName))
                senderName = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname;
            var senderUin = NapCatApiClient.ReadLong(src?["senderUin"]);
            if (senderUin <= 0) senderUin = _selfUin;
            var customNodes = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "node",
                    ["data"] = new JsonObject
                    {
                        ["user_id"] = senderUin.ToString(),
                        ["nickname"] = senderName,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["data"] = new JsonObject { ["text"] = srcText },
                            },
                        },
                    },
                },
            };
            (data, err) = await CallForwardMsgAsync(kind, peer, customNodes);
            newMid = NapCatApiClient.ReadLong(data?["message_id"]);
        }

        if (newMid <= 0)
        {
            // Last resort: native single-msg forward (no message_id ack).
            var singleAction = kind == 'g' ? "forward_group_single_msg" : "forward_friend_single_msg";
            var singleParams = new JsonObject { ["message_id"] = mid };
            if (kind == 'g') singleParams["group_id"] = peer;
            else singleParams["user_id"] = peer;
            var (singleData, singleErr) = await _api.CallAsync(singleAction, singleParams);
            if (singleErr != null && err != null) return (null, err ?? singleErr);
            data = singleData ?? data;
            err = singleErr;
            newMid = NapCatApiClient.ReadLong(data?["message_id"]);
            if (newMid <= 0 && singleErr == null)
            {
                // API claims ok with empty body — treat as soft success but still surface a bubble.
                Console.WriteLine("[NapCat] forward single-msg returned no message_id (ok with null data)");
            }
            else if (newMid <= 0)
                return (null, err ?? "forward-failed");
        }

        var text = "[转发消息]";
        if (src != null)
        {
            var srcText = NapCatApiClient.ReadStr(src["text"]);
            if (!string.IsNullOrEmpty(srcText)) text = "[转发] " + srcText;
        }
        var wire = BuildOutgoingWire(
            conversationId,
            contentType: "Text",
            text: text,
            imagePath: null,
            elements: new JsonArray { new JsonObject { ["Type"] = "Text", ["Text"] = text } },
            messageId: newMid);
        return (wire, null);
    }

    private Task<(JsonNode? data, string? error)> CallForwardMsgAsync(char kind, long peer, JsonArray messages)
    {
        var action = kind == 'g' ? "send_group_forward_msg" : "send_private_forward_msg";
        var parameters = new JsonObject { ["messages"] = messages };
        if (kind == 'g') parameters["group_id"] = peer;
        else parameters["user_id"] = peer;
        return _api.CallAsync(action, parameters);
    }

    /// <summary>Friend/group recall notice → remove cached wire + push messageRecalled to Shell.</summary>
    private void HandleRecallNotice(JsonObject node)
    {
        var mid = NapCatApiClient.ReadLong(node["message_id"]);
        if (mid <= 0) return;
        var operatorId = NapCatApiClient.ReadLong(node["operator_id"] ?? node["user_id"]);
        var userId = NapCatApiClient.ReadLong(node["user_id"]);
        var groupId = NapCatApiClient.ReadLong(node["group_id"]);
        string convId;
        if (groupId > 0) convId = "g" + groupId;
        else
        {
            var peer = userId > 0 && userId != _selfUin ? userId : operatorId;
            if (peer <= 0 || peer == _selfUin)
                peer = userId > 0 ? userId : operatorId;
            convId = "f" + peer;
        }

        string? wireId = null;
        string? preview = null;
        string? senderName = null;
        long senderUin = 0;
        lock (_gate)
        {
            if (_messages.TryGetValue(convId, out var list))
            {
                var hit = list.FirstOrDefault(m =>
                    NapCatApiClient.ReadLong(m["napcatMessageId"]) == mid
                    || ((string?)m["id"])?.EndsWith(":" + mid, StringComparison.Ordinal) == true);
                if (hit != null)
                {
                    wireId = (string?)hit["id"];
                    preview = NapCatApiClient.ReadStr(hit["text"]);
                    senderName = NapCatApiClient.ReadStr(hit["senderName"]);
                    senderUin = NapCatApiClient.ReadLong(hit["senderUin"]);
                    list.Remove(hit);
                    if (!string.IsNullOrEmpty(wireId)) _msgIndex.TryRemove(wireId, out _);
                }
            }
            // Also scan index if conv guess was wrong (self-echo edge cases).
            if (wireId == null)
            {
                foreach (var kv in _msgIndex.ToList())
                {
                    if (NapCatApiClient.ReadLong(kv.Value["napcatMessageId"]) != mid) continue;
                    wireId = kv.Key;
                    preview = NapCatApiClient.ReadStr(kv.Value["text"]);
                    senderName = NapCatApiClient.ReadStr(kv.Value["senderName"]);
                    senderUin = NapCatApiClient.ReadLong(kv.Value["senderUin"]);
                    convId = NapCatApiClient.ReadStr(kv.Value["conversationId"]);
                    if (_messages.TryGetValue(convId, out var list2))
                        list2.RemoveAll(m => (string?)m["id"] == wireId);
                    _msgIndex.TryRemove(kv.Key, out _);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(wireId))
            wireId = convId + ":" + mid;

        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "messageRecalled",
            ["data"] = new JsonObject
            {
                ["conversationId"] = convId,
                ["messageId"] = wireId,
                ["napcatMessageId"] = mid,
                ["operatorUin"] = operatorId,
                ["senderUin"] = senderUin > 0 ? senderUin : userId,
                ["senderName"] = senderName ?? "",
                ["preview"] = preview ?? "",
                ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            },
        }.ToJsonString());
    }

    public JsonObject SetConversationFlags(string conversationId, bool? isPinned, bool? isMuted)
    {
        if (string.IsNullOrEmpty(conversationId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-conversation" };
        if (isPinned == null && isMuted == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "no-flags" };

        lock (_gate)
        {
            _convPrefs.TryGetValue(conversationId, out var prev);
            var pinned = isPinned ?? prev.pinned;
            var muted = isMuted ?? prev.muted;
            _convPrefs[conversationId] = (pinned, muted);
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == conversationId);
            if (conv != null)
            {
                conv["isPinned"] = pinned;
                conv["isMuted"] = muted;
                if (muted) conv["unread"] = 0;
            }
            SavePrefs();
            return new JsonObject
            {
                ["ok"] = true,
                ["conversationId"] = conversationId,
                ["isPinned"] = pinned,
                ["isMuted"] = muted,
            };
        }
    }

    public JsonObject MarkConversationRead(string conversationId)
    {
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == conversationId);
            if (conv != null) conv["unread"] = 0;
        }
        // Best-effort NapCat cloud read marker (do not block UI).
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
        return new JsonObject { ["ok"] = true };
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

    // ---- helpers ----

    private static List<string> CollectImages(string? imageBase64, JsonNode? imagesBase64Node)
    {
        var list = new List<string>();
        void Add(string? b64)
        {
            if (string.IsNullOrWhiteSpace(b64)) return;
            if (list.Count > 0 && list[0] == b64) return;
            list.Add(b64);
        }
        Add(imageBase64);
        if (imagesBase64Node is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is JsonValue v && v.TryGetValue<string>(out var s)) Add(s);
            }
        }
        return list;
    }

    private static bool TryParseConv(string conversationId, out char kind, out long peer)
    {
        kind = '\0';
        peer = 0;
        if (string.IsNullOrEmpty(conversationId) || conversationId.Length < 2) return false;
        kind = conversationId[0];
        if (kind is not ('f' or 'g')) return false;
        return long.TryParse(conversationId.AsSpan(1), out peer) && peer > 0;
    }

    private static long ExtractNapCatMessageId(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return 0;
        if (long.TryParse(messageId, out var direct)) return direct;
        var parts = messageId.Split(':');
        return parts.Length >= 2 && long.TryParse(parts[^1], out var mid) ? mid : 0;
    }

    private static string FriendAvatarUrl(long uin) => $"https://q1.qlogo.cn/g?b=qq&nk={uin}&s=100";
    private static string GroupAvatarUrl(long groupUin) => $"https://p.qlogo.cn/gh/{groupUin}/{groupUin}/100";

    /// <summary>card → remark → nickname (skip opaque ids) → qid → uin.</summary>
    private static string PreferDisplayName(string card, string remark, string nickname, string qid, long uin)
    {
        if (!string.IsNullOrWhiteSpace(card)) return card.Trim();
        if (!string.IsNullOrWhiteSpace(remark)) return remark.Trim();
        if (!string.IsNullOrWhiteSpace(nickname) && !LooksLikeOpaqueId(nickname)) return nickname.Trim();
        if (!string.IsNullOrWhiteSpace(qid)) return qid.Trim();
        if (!string.IsNullOrWhiteSpace(nickname)) return nickname.Trim();
        return uin > 0 ? uin.ToString() : "未知";
    }

    /// <summary>UUID / session-token style strings that make bad chat display names.</summary>
    private static bool LooksLikeOpaqueId(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        // 8-4-4-4-12 hex UUID
        if (s.Length == 36 && s.Count(c => c == '-') == 4)
        {
            foreach (var c in s)
            {
                if (c == '-') continue;
                if (!Uri.IsHexDigit(c)) return false;
            }
            return true;
        }
        // long hex blob
        if (s.Length >= 32 && s.All(c => Uri.IsHexDigit(c))) return true;
        return false;
    }

    private static string MapRoleLabel(string roleRaw) => roleRaw?.Trim().ToLowerInvariant() switch
    {
        "owner" => "群主",
        "admin" => "管理员",
        "member" => "",
        _ => string.IsNullOrEmpty(roleRaw) ? "" : roleRaw,
    };

    private static int RoleSortKey(string roleRaw) => roleRaw?.Trim().ToLowerInvariant() switch
    {
        "owner" => 0,
        "admin" => 1,
        _ => 2,
    };

    private async Task<string> TryGetQidAsync(long uin)
    {
        try
        {
            var (data, err) = await _api.CallAsync("get_stranger_info", new JsonObject { ["user_id"] = uin });
            if (err != null || data == null) return "";
            var qid = NapCatApiClient.ReadStr(data["qid"]);
            if (!string.IsNullOrEmpty(qid)) return qid;
            // nick field sometimes cleaner than nickname
            var nick = NapCatApiClient.ReadStr(data["nick"]);
            if (!string.IsNullOrEmpty(nick) && !LooksLikeOpaqueId(nick)) return nick;
            return "";
        }
        catch { return ""; }
    }

    private static JsonObject Clone(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    private static bool IsTruthy(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private void ApplyPrefsTo(JsonObject conv)
    {
        var id = (string?)conv["id"];
        if (id != null && _convPrefs.TryGetValue(id, out var p))
        {
            conv["isPinned"] = p.pinned;
            conv["isMuted"] = p.muted;
        }
        else
        {
            conv["isPinned"] = false;
            conv["isMuted"] = false;
        }
    }

    private string PrefsPath => Path.Combine(AppContext.BaseDirectory, $"conv_prefs_napcat_{_prefsUin}.json");

    private void LoadPrefs(long uin)
    {
        _prefsUin = uin;
        _convPrefs.Clear();
        try
        {
            var path = PrefsPath;
            if (!File.Exists(path)) return;
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root == null) return;
            foreach (var kv in root)
            {
                if (kv.Value is not JsonObject o) continue;
                var pinned = o["isPinned"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
                var muted = o["isMuted"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) && mb;
                if (pinned || muted) _convPrefs[kv.Key] = (pinned, muted);
            }
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] LoadPrefs: " + ex.Message); }
    }

    private void SavePrefs()
    {
        try
        {
            if (_prefsUin <= 0) return;
            var root = new JsonObject();
            foreach (var kv in _convPrefs)
            {
                if (!kv.Value.pinned && !kv.Value.muted) continue;
                root[kv.Key] = new JsonObject { ["isPinned"] = kv.Value.pinned, ["isMuted"] = kv.Value.muted };
            }
            File.WriteAllText(PrefsPath, root.ToJsonString());
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] SavePrefs: " + ex.Message); }
    }
}
