using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
        public async Task<IReadOnlyList<Moment>> GetSpaceFeedAsync(bool forceRefresh = false)
        {
            var list = new List<Moment>();
            try
            {
                var raw = forceRefresh
                    ? await RequestAsync("fetchSpaceFeed", null, timeoutSeconds: 45)
                    : await RequestAsync("getMoments", null, timeoutSeconds: 45);
                if (string.IsNullOrEmpty(raw) || raw == "null") return list;
                var data = JsonObject.Parse(raw);
                if (data == null) return list;
                if (data.ContainsKey("hasMore"))
                    SpaceFeedHasMore = data.GetNamedBoolean("hasMore", SpaceFeedHasMore);
                var arr = data.GetNamedArray("moments", new JsonArray());
                foreach (var n in arr)
                {
                    if (n.ValueType != JsonValueType.Object) continue;
                    var o = n.GetObject();
                    var m = new Moment
                    {
                        Id = Str(o, "id"),
                        AuthorName = Str(o, "authorName"),
                        AuthorAvatarPath = Str(o, "authorAvatarPath"),
                        Text = Str(o, "text"),
                        TimeText = Str(o, "timeText"),
                        Time = Str(o, "time"),
                        VideoPath = Str(o, "videoPath"),
                        LikeCount = (int)o.GetNamedNumber("likeCount", 0),
                        IsLiked = o.GetNamedBoolean("isLiked", false),
                    };
                    // Optional like-name list from server extract.
                    try
                    {
                        if (o.ContainsKey("likers") && o.GetNamedValue("likers").ValueType == Windows.Data.Json.JsonValueType.Array)
                        {
                            var names = new System.Collections.Generic.List<string>();
                            foreach (var ln in o.GetNamedArray("likers"))
                            {
                                if (ln.ValueType == Windows.Data.Json.JsonValueType.String)
                                {
                                    var s = ln.GetString();
                                    if (!string.IsNullOrEmpty(s)) names.Add(s);
                                }
                            }
                            if (names.Count > 0)
                                m.LikersText = string.Join("、", names);
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(m.TimeText)) m.TimeText = Str(o, "time");
                    if (o.ContainsKey("images"))
                    {
                        var imgs = o.GetNamedArray("images");
                        foreach (var img in imgs)
                        {
                            if (img.ValueType == JsonValueType.String)
                            {
                                var u = img.GetString();
                                if (!string.IsNullOrEmpty(u)) m.ImagePaths.Add(u);
                            }
                        }
                    }
                    if (o.ContainsKey("comments"))
                    {
                        var comments = o.GetNamedArray("comments");
                        foreach (var comment in comments)
                        {
                            if (comment.ValueType != JsonValueType.Object) continue;
                            var c = comment.GetObject();
                            var mappedComment = new MomentComment
                            {
                                Author = Str(c, "author") ?? Str(c, "authorName"),
                                Text = Str(c, "text") ?? Str(c, "content"),
                            };
                            if (c.ContainsKey("replies") && c.GetNamedValue("replies").ValueType == JsonValueType.Array)
                            {
                                foreach (var reply in c.GetNamedArray("replies"))
                                {
                                    if (reply.ValueType != JsonValueType.Object) continue;
                                    var ro = reply.GetObject();
                                    mappedComment.Replies.Add(new MomentComment
                                    {
                                        Author = Str(ro, "author") ?? Str(ro, "authorName"),
                                        Text = Str(ro, "text") ?? Str(ro, "content"),
                                    });
                                }
                            }
                            m.Comments.Add(mappedComment);
                        }
                        m.RaiseCommentsChanged();
                    }
                    list.Add(m);
                }
            }
            catch { /* empty feed */ }
            return list;
        }

        /// <summary>Load older QQ 空间动态 (history pagination). Returns whether more pages exist.</summary>

        public async Task<bool> GetEarlierSpaceFeedAsync()
        {
            try
            {
                var raw = await RequestAsync("fetchEarlierSpaceFeed", r =>
                {
                    r["num"] = JsonValue.CreateNumberValue(20);
                });
                if (string.IsNullOrEmpty(raw) || raw == "null")
                {
                    SpaceFeedHasMore = false;
                    return false;
                }
                var data = JsonObject.Parse(raw);
                if (data == null)
                {
                    SpaceFeedHasMore = false;
                    return false;
                }
                var hasMore = data.GetNamedBoolean("hasMore", false);
                SpaceFeedHasMore = hasMore;
                return hasMore;
            }
            catch
            {
                return SpaceFeedHasMore;
            }
        }

        /// <summary>Whether more QQ 空间 history pages are available.
        /// Updated by the spaceFeedUpdated push (server now includes hasMore).</summary>
        public bool SpaceFeedHasMore { get; private set; } = true;


        public async Task<bool> SetSpaceCommentAsync(string momentId, string text)
        {
            if (string.IsNullOrWhiteSpace(momentId) || string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                var raw = await RequestAsync("setSpaceComment", r =>
                {
                    r["momentId"] = JsonValue.CreateStringValue(momentId);
                    r["text"] = JsonValue.CreateStringValue(text.Trim());
                }, timeoutSeconds: 30);
                if (string.IsNullOrEmpty(raw) || raw == "null") return false;
                var data = JsonObject.Parse(raw);
                return data?.GetNamedBoolean("ok", false) == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Bind this shell to the NapCat account already logged in on the PC gateway.
        /// Pass <paramref name="expectedUin"/> only when the client must match a specific QQ.
        /// Empty = adopt whatever NapCat reports. Null gateway response is treated as false.
        /// </summary>
    }
}
