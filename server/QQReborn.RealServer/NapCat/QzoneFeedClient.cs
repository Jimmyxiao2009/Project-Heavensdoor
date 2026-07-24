using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

/// <summary>
/// Fetches QQ Zone / 好友动态 using cookies from NapCat (get_cookies for qzone.qq.com).
/// Primary endpoint: h5.qzone.qq.com/webapp/json/mqzone_feeds/getActiveFeeds
/// </summary>
public sealed class QzoneFeedClient
{
    private readonly NapCatApiClient _api;
    private readonly long _selfUin;
    private string _attachInfo = "";
    private readonly List<JsonObject> _moments = new();
    private readonly object _gate = new();
    private bool _hasMore = true;

    public QzoneFeedClient(NapCatApiClient api, long selfUin)
    {
        _api = api;
        _selfUin = selfUin;
    }

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var m in _moments) arr.Add(m.DeepClone());
            return new JsonObject
            {
                ["moments"] = arr,
                ["hasMore"] = _hasMore,
                ["backend"] = "napcat-qzone",
            };
        }
    }

    public async Task<(int added, bool hasMore)> RefreshAsync(int pageSize = 20, bool earlier = false)
    {
        var cookies = await FetchCookiesAsync();
        if (string.IsNullOrEmpty(cookies))
        {
            Console.WriteLine("[Qzone] no cookies from NapCat get_cookies(qzone.qq.com)");
            return (0, false);
        }

        if (!TryGetCookie(cookies, "p_skey", out var pskey) && !TryGetCookie(cookies, "skey", out pskey))
        {
            Console.WriteLine("[Qzone] missing p_skey/skey");
            return (0, false);
        }

        var gtk = CalculateGtk(pskey);
        var attach = earlier ? _attachInfo : "";
        Console.WriteLine($"[Qzone] fetch earlier={earlier} gtk={gtk} attachLen={attach.Length}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 13; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 QQ/9.0.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://h5.qzone.qq.com/mqzone/index");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://h5.qzone.qq.com");

        // Prefer active friend feeds (rich vFeeds); fall back to mobile get_feeds.
        var urls = new List<string>
        {
            $"https://h5.qzone.qq.com/webapp/json/mqzone_feeds/getActiveFeeds?g_tk={gtk}&format=json"
                + (string.IsNullOrEmpty(attach) ? "" : $"&attachinfo={Uri.EscapeDataString(attach)}"),
            $"https://mobile.qzone.qq.com/get_feeds?res_type=2&res_attach={Uri.EscapeDataString(attach)}&refresh_type=2&format=json&g_tk={gtk}",
        };

        string? body = null;
        foreach (var url in urls)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                var text = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[Qzone] {url.Split('?')[0]} HTTP {(int)resp.StatusCode} len={text.Length}");
                if (!resp.IsSuccessStatusCode) continue;
                var t = UnwrapCallback(text);
                var probe = JsonNode.Parse(t);
                if (probe == null) continue;
                var code = ReadInt(probe["code"] ?? probe["ret"]);
                if (code != 0)
                {
                    Console.WriteLine($"[Qzone] code={code} msg={ReadStr(probe["message"] ?? probe["msg"])}");
                    continue;
                }
                body = t;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Qzone] request: " + ex.Message);
            }
        }

        if (body == null) return (0, false);

        var root = JsonNode.Parse(body);
        var data = root?["data"] as JsonObject ?? root as JsonObject;
        if (data == null) return (0, false);

        _attachInfo = ReadStr(data["attachinfo"] ?? data["attach_info"] ?? data["res_attach"]);
        var hasMoreRaw = data["hasmore"] ?? data["hasMore"] ?? data["hasMoreFeeds"];
        _hasMore = hasMoreRaw switch
        {
            JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
            JsonValue jv when jv.TryGetValue<int>(out var i) => i != 0,
            JsonValue jv when jv.TryGetValue<long>(out var l) => l != 0,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s is "1" or "true" or "True",
            _ => !string.IsNullOrEmpty(_attachInfo),
        };

        var feedList = data["vFeeds"] as JsonArray
            ?? data["feeds"] as JsonArray
            ?? data["feedlist"] as JsonArray
            ?? data["msglist"] as JsonArray;

        if (feedList == null || feedList.Count == 0)
        {
            Console.WriteLine("[Qzone] empty vFeeds");
            return (0, _hasMore);
        }

        var added = 0;
        lock (_gate)
        {
            if (!earlier) _moments.Clear();
            var existing = new HashSet<string>(_moments.Select(m => ReadStr(m["id"])));
            foreach (var node in feedList)
            {
                if (node is not JsonObject feed) continue;
                var moment = MapFeed(feed);
                if (moment == null) continue;
                var id = ReadStr(moment["id"]);
                if (string.IsNullOrEmpty(id) || existing.Contains(id)) continue;
                existing.Add(id);
                if (earlier) _moments.Add(moment);
                else _moments.Add(moment);
                added++;
            }
            // newest first
            _moments.Sort((a, b) => string.Compare(ReadStr(b["time"]), ReadStr(a["time"]), StringComparison.Ordinal));
        }

        Console.WriteLine($"[Qzone] mapped added={added} total={_moments.Count} hasMore={_hasMore}");
        return (added, _hasMore);
    }

    public async Task<bool> LikeAsync(string momentId, bool isLiked)
    {
        // Best-effort: many like CGIs need unikey/curkey from the feed. Store them on moment.
        JsonObject? moment;
        lock (_gate) moment = _moments.FirstOrDefault(m => ReadStr(m["id"]) == momentId);
        if (moment == null) return false;

        var cookies = await FetchCookiesAsync();
        if (string.IsNullOrEmpty(cookies) || !TryGetCookie(cookies, "p_skey", out var pskey))
            return false;
        var gtk = CalculateGtk(pskey);
        var unikey = ReadStr(moment["unikey"]);
        var curkey = ReadStr(moment["curkey"]);
        if (string.IsNullOrEmpty(unikey)) unikey = momentId;
        if (string.IsNullOrEmpty(curkey)) curkey = unikey;

        var op = isLiked ? 0 : 1; // 0=like 1=unlike in some APIs; we try both shapes
        var url = $"https://h5.qzone.qq.com/proxy/domain/w.qzone.qq.com/cgi-bin/likes/internal_dolike_app?g_tk={gtk}";
        var form = new Dictionary<string, string>
        {
            ["opuin"] = _selfUin.ToString(),
            ["unikey"] = unikey,
            ["curkey"] = curkey,
            ["appid"] = ReadStr(moment["appid"]).Length > 0 ? ReadStr(moment["appid"]) : "311",
            ["opr_type"] = "like",
            ["format"] = "json",
            ["from"] = "1",
        };
        if (!isLiked) form["op"] = "1";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://h5.qzone.qq.com/");
            using var content = new FormUrlEncodedContent(form);
            using var resp = await http.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Qzone] like HTTP {(int)resp.StatusCode} {text[..Math.Min(120, text.Length)]}");
            var ok = text.Contains("\"code\":0") || text.Contains("\"ret\":0") || resp.IsSuccessStatusCode;
            if (ok)
            {
                lock (_gate)
                {
                    moment["isLiked"] = isLiked;
                    var c = ReadLong(moment["likeCount"]);
                    moment["likeCount"] = Math.Max(0, c + (isLiked ? 1 : -1));
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Qzone] like: " + ex.Message);
            return false;
        }
    }

    private async Task<string> FetchCookiesAsync()
    {
        // Prefer domain=qzone.qq.com (has p_skey)
        foreach (var domain in new[] { "qzone.qq.com", "user.qzone.qq.com" })
        {
            var (data, err) = await _api.CallAsync("get_cookies", new JsonObject { ["domain"] = domain });
            if (err != null) continue;
            var cookies = ReadStr(data?["cookies"]);
            if (!string.IsNullOrEmpty(cookies) && cookies.Contains("p_skey=", StringComparison.Ordinal))
                return cookies;
            if (!string.IsNullOrEmpty(cookies)) return cookies;
        }
        return "";
    }

    private JsonObject? MapFeed(JsonObject feed)
    {
        try
        {
            var userinfo = feed["userinfo"] as JsonObject ?? feed["user"] as JsonObject;
            var user = userinfo?["user"] as JsonObject ?? userinfo;
            var comm = feed["comm"] as JsonObject ?? feed["cell_comm"] as JsonObject;
            var summaryNode = feed["summary"];
            var like = feed["like"] as JsonObject;
            var comment = feed["comment"] as JsonObject;

            var id = ReadStr(feed["id"] is JsonObject idObj
                ? (idObj["cellid"] ?? idObj["cellId"])
                : feed["id"]);
            if (string.IsNullOrEmpty(id)) id = ReadStr(comm?["feedskey"] ?? comm?["curlikekey"]);
            if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N");

            var text = "";
            if (summaryNode is JsonObject so)
                text = ReadStr(so["summary"] ?? so["content"] ?? so["title"] ?? so["text"]);
            else
                text = ReadStr(summaryNode);
            if (string.IsNullOrEmpty(text)) text = ReadStr(feed["content"]);

            var authorUin = ReadLong(user?["uin"] ?? user?["u"]);
            var authorName = ReadStr(user?["nickname"] ?? user?["name"]);
            // UUID-like QQ nick (common after bot sessions) → fall back to uin string for display
            if (LooksOpaque(authorName)) authorName = "";
            if (string.IsNullOrEmpty(authorName))
                authorName = authorUin > 0 ? authorUin.ToString() : "好友";
            var avatar = ReadStr(user?["logo"]);
            if (string.IsNullOrEmpty(avatar) && authorUin > 0)
                avatar = $"https://q1.qlogo.cn/g?b=qq&nk={authorUin}&s=100";

            var ts = ReadLong(comm?["time"] ?? feed["created_time"] ?? feed["time"]);
            if (ts > 10_000_000_000) ts /= 1000;
            var timeIso = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).ToString("o") : DateTimeOffset.UtcNow.ToString("o");
            var timeText = ts > 0 ? RelativeTime(ts) : "";

            var likeCount = (int)ReadLong(like?["num"] ?? like?["likeNum"] ?? like?["like_num"]);
            var isLiked = ReadLong(like?["isliked"] ?? like?["isLiked"] ?? like?["is_liked"]) != 0
                || (like?["isliked"] is JsonValue jv && jv.TryGetValue<bool>(out var lb) && lb);

            var images = new JsonArray();
            CollectPics(feed["pic"] as JsonObject, images);
            CollectPics(feed["cell_pic"] as JsonObject, images);
            CollectPics(feed["picture"] as JsonObject, images);

            var comments = new JsonArray();
            var cmtArr = comment?["comments"] as JsonArray
                ?? comment?["commentlist"] as JsonArray
                ?? comment?["vctCommItems"] as JsonArray;
            if (cmtArr != null)
            {
                foreach (var c in cmtArr)
                {
                    if (c is not JsonObject co) continue;
                    var cu = co["user"] as JsonObject ?? co["userinfo"] as JsonObject;
                    var cname = ReadStr(cu?["nickname"] ?? co["name"] ?? co["uin"]);
                    var ctext = ReadStr(co["content"] ?? co["summary"] ?? co["comment"]);
                    if (string.IsNullOrEmpty(ctext)) continue;
                    comments.Add(new JsonObject { ["author"] = cname, ["text"] = ctext });
                }
            }

            var likers = new JsonArray();
            var likeList = like?["list"] as JsonArray ?? like?["likemans"] as JsonArray ?? like?["items"] as JsonArray;
            if (likeList != null)
            {
                foreach (var ln in likeList)
                {
                    if (ln is JsonObject lo)
                    {
                        var n = ReadStr(lo["nickname"] ?? lo["name"] ?? lo["uin"]);
                        if (!string.IsNullOrEmpty(n)) likers.Add(n);
                    }
                    else if (ln is JsonValue lv && lv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                        likers.Add(s);
                }
            }

            return new JsonObject
            {
                ["id"] = id,
                ["authorName"] = authorName,
                ["authorAvatarPath"] = avatar,
                ["authorUin"] = authorUin,
                ["text"] = text,
                ["timeText"] = timeText,
                ["time"] = timeIso,
                ["images"] = images,
                ["videoPath"] = "",
                ["likeCount"] = likeCount,
                ["isLiked"] = isLiked,
                ["likers"] = likers,
                ["comments"] = comments,
                ["unikey"] = ReadStr(comm?["orglikekey"] ?? like?["orglikekey"]),
                ["curkey"] = ReadStr(comm?["curlikekey"] ?? like?["curlikekey"]),
                ["appid"] = ReadStr(comm?["appid"]),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Qzone] map feed: " + ex.Message);
            return null;
        }
    }

    private static void CollectPics(JsonObject? picRoot, JsonArray images)
    {
        if (picRoot == null) return;
        var picdata = picRoot["picdata"] as JsonArray
            ?? picRoot["pic_data"] as JsonArray
            ?? picRoot["images"] as JsonArray;
        if (picdata == null && picRoot["picdata"] is JsonObject single)
            picdata = new JsonArray { single };
        if (picdata == null) return;
        foreach (var p in picdata)
        {
            if (p is not JsonObject po) continue;
            var url = "";
            if (po["photourl"] is JsonObject purl)
            {
                foreach (var key in new[] { "11", "1", "0", "14", "2" })
                {
                    if (purl[key] is JsonObject uo)
                    {
                        url = ReadStr(uo["url"]);
                        if (!string.IsNullOrEmpty(url)) break;
                    }
                }
            }
            if (string.IsNullOrEmpty(url)) url = ReadStr(po["url"] ?? po["origin_url"] ?? po["bigurl"]);
            if (string.IsNullOrEmpty(url) && po["busi_param"] is JsonObject bp)
                url = ReadStr(bp["6"] ?? bp["-1"] ?? bp["1"]);
            if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                images.Add(url);
        }
    }

    private static string UnwrapCallback(string text)
    {
        var t = text.Trim();
        foreach (var wrap in new[] { "_Callback", "_preloadCallback", "callback" })
        {
            var p = wrap + "(";
            if (t.StartsWith(p, StringComparison.Ordinal) && t.EndsWith(')'))
            {
                t = t[p.Length..^1].Trim();
                break;
            }
        }
        if (t.StartsWith("while(1);")) t = t["while(1);".Length..].Trim();
        return t;
    }

    private static bool TryGetCookie(string cookies, string name, out string value)
    {
        value = "";
        foreach (var part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            if (!part[..i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = part[(i + 1)..];
            return !string.IsNullOrEmpty(value);
        }
        return false;
    }

    public static long CalculateGtk(string key)
    {
        long hash = 5381;
        foreach (var ch in key)
            hash += (hash << 5) + ch;
        return hash & 0x7fffffff;
    }

    private static bool LooksOpaque(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.Length == 36 && s.Count(c => c == '-') == 4;
    }

    private static string RelativeTime(long unixSec)
    {
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixSec);
            var span = DateTimeOffset.Now - dt;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " 分钟前";
            if (span.TotalDays < 1) return (int)span.TotalHours + " 小时前";
            if (span.TotalDays < 7) return (int)span.TotalDays + " 天前";
            return dt.ToLocalTime().ToString("MM-dd HH:mm");
        }
        catch { return ""; }
    }

    private static string ReadStr(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s ?? "",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(),
        JsonValue v when v.TryGetValue<double>(out var d) => ((long)d).ToString(),
        JsonObject o => ReadStr(o["summary"] ?? o["content"] ?? o["title"] ?? o["text"]),
        _ => n.ToJsonString().Trim('"'),
    };

    private static long ReadLong(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return 0;
    }

    private static int ReadInt(JsonNode? n) => (int)ReadLong(n);
}
