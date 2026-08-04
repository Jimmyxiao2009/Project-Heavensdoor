using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

public sealed partial class NapCatSessionManager
{
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

    private static DateTimeOffset? ParseWireTime(JsonObject wire)
    {
        var raw = NapCatApiClient.ReadStr(wire["time"]);
        return DateTimeOffset.TryParse(raw, out var value) ? value : null;
    }

    /// <summary>Keep the server transcript chronologically ordered even when the
    /// event stream delivers a delayed message after a newer one.</summary>
    private static void InsertMessageInTimeOrder(List<JsonObject> list, JsonObject wire)
    {
        var time = NapCatApiClient.ReadStr(wire["time"]);
        var index = list.FindIndex(existing =>
            string.Compare(NapCatApiClient.ReadStr(existing["time"]), time, StringComparison.Ordinal) > 0);
        if (index < 0) list.Add(wire);
        else list.Insert(index, wire);
    }


    /// <summary>
    /// Fill conversationTitle / conversationAvatarPath so Shell toasts can show the
    /// group (or friend) identity instead of only the individual sender.
    /// </summary>

    private void ApplyRecentContacts(JsonNode? recentData)
    {
        JsonArray? arr = recentData as JsonArray
            ?? recentData?["data"] as JsonArray
            ?? recentData?["list"] as JsonArray;
        if (arr == null) return;
        lock (_gate)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var peerUin = NapCatApiClient.ReadLong(o["peerUin"] ?? o["user_id"] ?? o["group_id"] ?? o["uin"]);
                if (peerUin <= 0) continue;
                var chatType = (int)NapCatApiClient.ReadLong(o["chatType"] ?? o["chat_type"]);
                var isGroup = chatType == 2 || chatType == 3;
                var convId = (isGroup ? "g" : "f") + peerUin;
                var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == convId);
                if (conv == null)
                {
                    var title = NapCatApiClient.ReadStr(o["peerName"] ?? o["remark"] ?? o["nickname"]) ?? peerUin.ToString();
                    conv = new JsonObject
                    {
                        ["id"] = convId,
                        ["kind"] = isGroup ? "Group" : "Friend",
                        ["title"] = title,
                        ["avatarPath"] = isGroup ? GroupAvatarUrl(peerUin) : FriendAvatarUrl(peerUin),
                        ["preview"] = "",
                        ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["unread"] = 0,
                    };
                    ApplyPrefsTo(conv);
                    _conversations.Add(conv);
                }
                var peerName = NapCatApiClient.ReadStr(o["peerName"] ?? o["remark"]);
                if (!string.IsNullOrWhiteSpace(peerName))
                {
                    var cur = (string?)conv["title"] ?? "";
                    if (string.IsNullOrWhiteSpace(cur) || cur == convId || cur == peerUin.ToString())
                        conv["title"] = peerName;
                }
                var msgTime = NapCatApiClient.ReadLong(o["msgTime"] ?? o["time"]);
                if (msgTime > 0)
                {
                    try
                    {
                        var dto = msgTime > 3_000_000_000
                            ? DateTimeOffset.FromUnixTimeMilliseconds(msgTime)
                            : DateTimeOffset.FromUnixTimeSeconds(msgTime);
                        conv["lastTime"] = dto.ToString("o");
                    }
                    catch { }
                }
                if (o["lastestMsg"] is JsonObject lm)
                {
                    var raw = NapCatApiClient.ReadStr(lm["raw_message"] ?? lm["message"] ?? lm["text"]);
                    if (!string.IsNullOrEmpty(raw))
                        conv["preview"] = raw.Length > 80 ? raw[..80] : raw;
                }
                if (o.ContainsKey("unreadCnt") || o.ContainsKey("unread") || o.ContainsKey("unread_count"))
                {
                    var unread = (int)NapCatApiClient.ReadLong(o["unreadCnt"] ?? o["unread"] ?? o["unread_count"]);
                    if (unread < 0) unread = 0;
                    conv["unread"] = unread;
                    if (!_convPrefs.TryGetValue(convId, out var pref) || pref == null)
                        pref = new ConvPref();
                    pref.Unread = unread;
                    _convPrefs[convId] = pref;
                }
            }
            SavePrefs();
        }
    }

    private void EnrichConversationMeta(JsonObject wire, string conversationId, string? preferredTitle = null)
    {
        if (wire == null || string.IsNullOrEmpty(conversationId)) return;

        string? title = preferredTitle;
        string? avatar = null;
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string?)c["id"] == conversationId);
            if (conv != null)
            {
                title = string.IsNullOrWhiteSpace(title) ? (string?)conv["title"] : title;
                avatar = (string?)conv["avatarPath"];
            }
        }

        long peer = 0;
        if (conversationId.Length > 1)
            long.TryParse(conversationId.AsSpan(1), out peer);

        var isGroup = conversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(avatar) && peer > 0)
            avatar = isGroup ? GroupAvatarUrl(peer) : FriendAvatarUrl(peer);
        if (string.IsNullOrWhiteSpace(title))
            title = peer > 0 ? peer.ToString() : conversationId;

        wire["conversationTitle"] = title ?? "";
        wire["conversationAvatarPath"] = avatar ?? "";
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
        if (id != null && _convPrefs.TryGetValue(id, out var pref) && pref != null)
        {
            conv["isPinned"] = pref.Pinned;
            conv["isMuted"] = pref.Muted;
            if (!string.IsNullOrEmpty(pref.LastReadAt))
                conv["lastReadAt"] = pref.LastReadAt;
            if (pref.Unread > 0)
            {
                var cur = (int)NapCatApiClient.ReadLong(conv["unread"]);
                if (pref.Unread > cur) conv["unread"] = pref.Unread;
            }
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
                var pref = new ConvPref
                {
                    Pinned = o["isPinned"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb,
                    Muted = o["isMuted"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) && mb,
                    LastReadAt = o["lastReadAt"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : null,
                    Unread = o["unread"] is JsonValue uv && uv.TryGetValue<int>(out var ui) ? ui
                        : o["unread"] is JsonValue uv2 && uv2.TryGetValue<double>(out var ud) ? (int)ud : 0,
                };
                if (pref.Pinned || pref.Muted || pref.Unread > 0 || !string.IsNullOrEmpty(pref.LastReadAt))
                    _convPrefs[kv.Key] = pref;
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
                var pref = kv.Value;
                if (pref == null) continue;
                if (!pref.Pinned && !pref.Muted && pref.Unread <= 0 && string.IsNullOrEmpty(pref.LastReadAt))
                    continue;
                var o = new JsonObject
                {
                    ["isPinned"] = pref.Pinned,
                    ["isMuted"] = pref.Muted,
                    ["unread"] = pref.Unread,
                };
                if (!string.IsNullOrEmpty(pref.LastReadAt)) o["lastReadAt"] = pref.LastReadAt;
                root[kv.Key] = o;
            }
            File.WriteAllText(PrefsPath, root.ToJsonString());
        }
        catch (Exception ex) { Console.WriteLine("[NapCat] SavePrefs: " + ex.Message); }
    }
}
