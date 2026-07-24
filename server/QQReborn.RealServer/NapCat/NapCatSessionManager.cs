using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

/// <summary>
/// OneBot 11 / NapCat-backed session that speaks the same App wire protocol as
/// <see cref="BotSessionManager"/>. Login is owned by NapCat/NTQQ; configureAccount
/// only verifies connectivity and starts the event stream.
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

            if (!string.IsNullOrWhiteSpace(signUinRaw)
                && long.TryParse(signUinRaw, out var expect)
                && expect > 0
                && expect != uin)
            {
                return (null, $"NapCat 当前登录 {uin}，与 App 填写的 {expect} 不一致");
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
            BroadcastLoginStatus("online", uin, nick);
            return (new JsonObject { ["accepted"] = true, ["backend"] = BackendId, ["uin"] = uin, ["nickname"] = nick }, null);
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
            // Friend request etc. — best-effort
            var notice = NapCatApiClient.ReadStr(node["notice_type"]);
            if (notice is "friend_add" or "friend_request")
                Console.WriteLine("[NapCat] notice: " + notice);
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
                    var name = NapCatApiClient.ReadStr(o["remark"]);
                    if (string.IsNullOrEmpty(name)) name = NapCatApiClient.ReadStr(o["nickname"]);
                    if (string.IsNullOrEmpty(name)) name = uin.ToString();
                    var avatar = FriendAvatarUrl(uin);
                    _contacts.Add(new JsonObject
                    {
                        ["uin"] = uin,
                        ["name"] = name,
                        ["avatarPath"] = avatar,
                        ["signature"] = NapCatApiClient.ReadStr(o["longNick"] ?? o["signature"]),
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

        var (data, err) = await _api.CallAsync("get_group_member_list", new JsonObject { ["group_id"] = peer });
        if (err != null)
        {
            Console.WriteLine("[NapCat] get_group_member_list: " + err);
            return arr;
        }
        var members = data as JsonArray ?? data?["members"] as JsonArray;
        if (members == null) return arr;
        foreach (var n in members)
        {
            if (n is not JsonObject o) continue;
            var uin = NapCatApiClient.ReadLong(o["user_id"]);
            var name = NapCatApiClient.ReadStr(o["card"]);
            if (string.IsNullOrEmpty(name)) name = NapCatApiClient.ReadStr(o["nickname"]);
            arr.Add(new JsonObject
            {
                ["uin"] = uin,
                ["name"] = name,
                ["avatarPath"] = FriendAvatarUrl(uin),
                ["role"] = NapCatApiClient.ReadStr(o["role"]),
            });
        }
        return arr;
    }

    public JsonArray GetFriendRequests() => new();

    public JsonObject AcceptFriendRequest(long uin)
        => new() { ["ok"] = false, ["reason"] = "napcat: use NapCat/NTQQ UI for friend requests (wire stub)" };

    public async Task<(JsonObject? data, string? error)> GetUserProfileAsync(long uin)
    {
        var (data, err) = await _api.CallAsync("get_stranger_info", new JsonObject { ["user_id"] = uin });
        if (err != null)
        {
            // fallback minimal
            return (new JsonObject
            {
                ["uin"] = uin,
                ["nickname"] = uin.ToString(),
                ["avatarPath"] = FriendAvatarUrl(uin),
                ["signature"] = "",
            }, null);
        }
        var nick = NapCatApiClient.ReadStr(data?["nickname"]);
        return (new JsonObject
        {
            ["uin"] = uin,
            ["nickname"] = string.IsNullOrEmpty(nick) ? uin.ToString() : nick,
            ["avatarPath"] = FriendAvatarUrl(uin),
            ["signature"] = NapCatApiClient.ReadStr(data?["longNick"] ?? data?["sign"]),
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

    public Task<(JsonObject? data, string? error)> SetAvatarAsync(string imageBase64)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: set avatar not wired"));

    public Task<(JsonObject? data, string? error)> GetMediaUrlAsync(string messageId)
    {
        if (_msgIndex.TryGetValue(messageId, out var wire))
        {
            var url = (string?)wire["imagePath"] ?? "";
            if (string.IsNullOrEmpty(url) && wire["elements"] is JsonArray els)
            {
                foreach (var e in els)
                {
                    if (e is JsonObject o && NapCatApiClient.ReadStr(o["Type"]) is "Image" or "image")
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

    public Task<(JsonObject? data, string? error)> GetVoicePlayableAsync(string messageId)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: voice playable not wired"));

    public Task<(JsonObject? data, string? error)> GetFileDownloadUrlAsync(string conversationId, string fileId)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: file download not wired"));

    public Task<(JsonObject? data, string? error)> GetGroupNotificationsAsync()
        => Task.FromResult<(JsonObject?, string?)>((new JsonObject { ["notifications"] = new JsonArray() }, null));

    public Task<(JsonObject? data, string? error)> HandleGroupNotificationAsync(
        long groupUin, ulong sequence, string notifType, string operate, string? message, bool isFiltered)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: group notification operate not wired"));

    public Task<(JsonObject? data, string? error)> SetGroupReactionAsync(
        string conversationId, string messageId, string code, bool isAdd)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: reaction not wired"));

    public JsonObject GetSpaceFeed()
        => new() { ["moments"] = new JsonArray(), ["hasMore"] = false, ["backend"] = BackendId };

    public JsonObject SetSpaceLike(string momentId, bool isLiked)
        => new() { ["ok"] = false, ["reason"] = "napcat: moments not available" };

    public JsonObject IngestSpaceWebhook(JsonNode? body)
        => new() { ["ok"] = false, ["reason"] = "napcat: space webhook not used" };

    public Task FetchQzoneFeedNativeAsync() => Task.CompletedTask;

    public Task<JsonObject> FetchEarlierSpaceFeedAsync(int num = 20)
        => Task.FromResult(new JsonObject { ["added"] = 0, ["hasMore"] = false });

    public async Task<(JsonObject? data, string? error)> SendAsync(
        string conversationId, string text, string? replyToId = null,
        string contentType = "Text", string? placeName = null, string? address = null, string? thumb = null,
        string? imageBase64 = null, JsonNode? imagesBase64Node = null, string? audioBase64 = null, int voiceSeconds = 0,
        string? fileBase64 = null, string? fileName = null, string? mentionsJson = null)
    {
        if (!_online) return (null, "not-online");
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");

        var segments = new JsonArray();
        if (!string.IsNullOrEmpty(replyToId))
        {
            var mid = ExtractNapCatMessageId(replyToId);
            if (mid > 0)
                segments.Add(new JsonObject { ["type"] = "reply", ["data"] = new JsonObject { ["id"] = mid.ToString() } });
        }

        // Caption text
        if (contentType is "Text" or "Mixed" or "" || (contentType is "Image" or "Sticker" && !string.IsNullOrWhiteSpace(text)))
        {
            if (!string.IsNullOrWhiteSpace(text) && contentType != "Location")
                segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = text } });
        }
        if (contentType == "Location")
        {
            var loc = string.IsNullOrEmpty(address) ? $"[位置] {placeName}" : $"[位置] {placeName}（{address}）";
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = loc } });
        }

        // Images
        var images = CollectImages(imageBase64, imagesBase64Node);
        foreach (var b64 in images)
        {
            segments.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = new JsonObject { ["file"] = "base64://" + b64 },
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

        if (contentType == "File" && !string.IsNullOrEmpty(fileBase64))
        {
            // OneBot file segment support varies; send as text notice for now if unsupported
            segments.Add(new JsonObject
            {
                ["type"] = "text",
                ["data"] = new JsonObject { ["text"] = $"[文件] {fileName ?? "file"}（NapCat 文件直传待完善）" },
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

        var (data, err) = await _api.CallAsync(action, parameters);
        if (err != null) return (null, err);

        var messageId = NapCatApiClient.ReadLong(data?["message_id"]);
        var id = $"{conversationId}:{messageId}";
        var (wireType, wireText, imagePath, elements) = MapSegments(segments);
        if (contentType is "Image" or "Sticker" or "Mixed" && images.Count > 0 && string.IsNullOrEmpty(imagePath))
        {
            // outbound base64 won't have CDN yet — leave empty; client patches local
            wireType = !string.IsNullOrWhiteSpace(text) && images.Count > 0 ? "Mixed"
                : images.Count > 1 ? "Mixed" : "Image";
            if (string.IsNullOrEmpty(wireText)) wireText = images.Count > 1 ? $"[图片×{images.Count}]" : "[图片]";
        }

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

    public Task<(JsonObject? data, string? error)> ForwardAsync(string conversationId, string messageId)
        => Task.FromResult<(JsonObject?, string?)>((null, "napcat: forward not wired"));

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
