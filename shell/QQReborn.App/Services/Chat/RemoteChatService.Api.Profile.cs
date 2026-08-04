using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
        public async Task<IReadOnlyList<FriendRequest>> GetFriendRequestsAsync()
        {
            var arr = JsonArray.Parse(await RequestAsync("getFriendRequests", null));
            var list = new List<FriendRequest>();
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new FriendRequest
                {
                    Uin = (long)o.GetNamedNumber("uin", 0),
                    Name = Str(o, "name"),
                    AvatarPath = Str(o, "avatarPath"),
                    Message = Str(o, "message"),
                    Handled = o.GetNamedBoolean("handled", false),
                });
            }
            return list;
        }


        public async Task AcceptFriendRequestAsync(FriendRequest request)
        {
            if (request == null) return;
            var data = JsonObject.Parse(await RequestAsync("acceptFriendRequest",
                r => r["uin"] = JsonValue.CreateNumberValue(request.Uin)));
            // Honor whatever the backend actually did (handled:false on failure).
            request.Handled = data.GetNamedBoolean("handled", false);
        }


        public async Task RejectFriendRequestAsync(FriendRequest request)
        {
            if (request == null) return;
            var data = JsonObject.Parse(await RequestAsync("rejectFriendRequest",
                r => r["uin"] = JsonValue.CreateNumberValue(request.Uin)));
            request.Handled = data.GetNamedBoolean("handled", false);
        }

        /// <summary>给好友 QQ 名片点赞（每天上限 10 次）</summary>

        public async Task<bool> SendLikeAsync(long targetUin, int count = 1)
        {
            var data = JsonObject.Parse(await RequestAsync("sendLike", r =>
            {
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["count"] = JsonValue.CreateNumberValue(count);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>Fetches full profile detail for an arbitrary user (contact-detail page etc).
        /// signature/gender/country/city may come back as JSON null from the server, hence the
        /// null-safe Str() helper rather than GetNamedString.</summary>

        public async Task<UserProfile> GetUserProfileAsync(long uin)
        {
            var data = JsonObject.Parse(await RequestAsync("getUserProfile",
                r => r["uin"] = JsonValue.CreateNumberValue(uin)));
            return new UserProfile
            {
                Uin = (long)data.GetNamedNumber("uin", 0),
                Nickname = Str(data, "nickname"),
                Signature = Str(data, "signature"),
                Level = (int)data.GetNamedNumber("level", 0),
                Gender = Str(data, "gender"),
                Age = (int)data.GetNamedNumber("age", 0),
                Country = Str(data, "country"),
                City = Str(data, "city"),
            };
        }

        /// <summary>Pages in older messages from the cloud (infinite-scroll-up).
        /// <paramref name="beforeMessageId"/> may be null/empty to request the newest cloud
        /// page (used when the conversation has no local anchor yet). Message payloads reuse
        /// the same shape as getMessages, so ParseMessage handles each entry the same way.</summary>

        public async Task<bool> SetFriendRemarkAsync(long uin, string remark)
        {
            var data = JsonObject.Parse(await RequestAsync("setFriendRemark", r =>
            {
                r["uin"] = JsonValue.CreateNumberValue(uin);
                r["remark"] = JsonValue.CreateStringValue(remark ?? "");
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>删除好友 — NapCat delete_friend</summary>

        public async Task<bool> DeleteFriendAsync(long uin, bool tempBlock = false, bool bothDel = false)
        {
            var data = JsonObject.Parse(await RequestAsync("deleteFriend", r =>
            {
                r["uin"] = JsonValue.CreateNumberValue(uin);
                r["tempBlock"] = JsonValue.CreateBooleanValue(tempBlock);
                r["bothDel"] = JsonValue.CreateBooleanValue(bothDel);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>修改自己的昵称/签名 — set_qq_profile / set_self_longnick</summary>

        public async Task<bool> SetSelfProfileAsync(string nickname = null, string signature = null)
        {
            var data = JsonObject.Parse(await RequestAsync("setSelfProfile", r =>
            {
                if (nickname != null) r["nickname"] = JsonValue.CreateStringValue(nickname);
                if (signature != null) r["signature"] = JsonValue.CreateStringValue(signature);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>设置在线状态 — NapCat set_online_status
        /// 常用: 在线10 / 离开30 / 隐身40 / 忙碌50 / 请勿打扰70</summary>

        public async Task<bool> SetOnlineStatusAsync(int status, int extStatus = 0, int batteryStatus = 0)
        {
            var data = JsonObject.Parse(await RequestAsync("setOnlineStatus", r =>
            {
                r["status"] = JsonValue.CreateNumberValue(status);
                r["extStatus"] = JsonValue.CreateNumberValue(extStatus);
                r["batteryStatus"] = JsonValue.CreateNumberValue(batteryStatus);
            }));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>获取群公告列表 — NapCat _get_group_notice</summary>

        public async Task<long> GetProfileLikeCountAsync(long uin = 0)
        {
            var data = JsonObject.Parse(await RequestAsync("getProfileLike", r =>
            {
                if (uin > 0) r["uin"] = JsonValue.CreateNumberValue(uin);
            }));
            return data != null ? (long)data.GetNamedNumber("total", 0) : 0;
        }


        public async Task<string> GetUserStatusTextAsync(long uin)
        {
            var data = JsonObject.Parse(await RequestAsync("getUserStatus",
                r => r["uin"] = JsonValue.CreateNumberValue(uin)));
            if (data == null) return "";
            var st = (long)data.GetNamedNumber("status", 0);
            switch (st)
            {
                case 10: return "在线";
                case 30: return "离开";
                case 40: return "隐身";
                case 50: return "忙碌";
                case 60: return "Q我吧";
                case 70: return "请勿打扰";
                default: return st > 0 ? ("状态 " + st) : "";
            }
        }


        public async Task<string> GetVersionInfoSummaryAsync()
        {
            var data = JsonObject.Parse(await RequestAsync("getVersionInfo", null));
            if (data == null) return "";
            var ver = data.GetNamedValue("version");
            return ver != null ? ver.Stringify() : data.Stringify();
        }


        public async Task<bool> SetAvatarAsync(string imageBase64)
        {
            var data = JsonObject.Parse(await RequestAsync("setAvatar",
                r => r["imageBase64"] = JsonValue.CreateStringValue(imageBase64)));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>Resolves a downloadable URL for a message's media (e.g. video) payload.
        /// data.url may come back as JSON null, hence the null-safe Str() helper rather than
        /// GetNamedString.</summary>

        public async Task<bool> SetSpaceLikeAsync(string momentId, bool isLiked)
        {
            if (string.IsNullOrEmpty(momentId)) return false;
            try
            {
                var raw = await RequestAsync("setSpaceLike", r =>
                {
                    r["momentId"] = JsonValue.CreateStringValue(momentId);
                    r["isLiked"] = JsonValue.CreateBooleanValue(isLiked);
                });
                if (string.IsNullOrEmpty(raw) || raw == "null") return false;
                var data = JsonObject.Parse(raw);
                return data?.GetNamedBoolean("ok", false) == true;
            }
            catch
            {
                return false;
            }
        }

    }
}
