using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace QQReborn.FakeServer;

/// <summary>
/// In-memory fake QQ backend. Holds seed data, handles sends, and pushes
/// typing + auto-reply events to all connected clients. Mirrors what the old
/// in-app MockChatService did, but now lives server-side.
/// </summary>
public class ChatState
{
    private const string SelfAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
    private const string FriendAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
    private const string GroupAvatar = "ms-appx:///Assets/Avatars/DefaultTroopAvatar.png";

    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _messages = new();
    private readonly List<JsonObject> _conversations = new();
    private readonly List<JsonObject> _contacts = new();
    private readonly Dictionary<string, List<JsonObject>> _groupMembers = new();
    private readonly List<JsonObject> _friendRequests = new();
    private readonly Random _rng = new();
    private int _idSeed;

    /// <summary>Raised to broadcast an event frame (already a JSON string) to every client.</summary>
    public event Action<string>? Broadcast;

    public ChatState()
    {
        _conversations.Add(Conv("c1", "Friend", "张三", FriendAvatar, "在吗？晚上一起吃饭", -3, 2));
        _conversations.Add(Conv("c2", "Group", "WP 钉子户交流群", GroupAvatar, "李四：Lumia 950 永不为奴", -25, 9));
        _conversations.Add(Conv("c3", "Friend", "老妈", FriendAvatar, "记得多穿点衣服", -120, 0));
        _conversations.Add(Conv("c4", "Group", "家庭群", GroupAvatar, "[图片]", -1440, 0));
        _conversations.Add(Conv("c5", "Friend", "QQ 团队", FriendAvatar, "欢迎使用 QQ Reborn", -4320, 0));

        _contacts.Add(Contact(20001, "张三", "今天也要加油", true));
        _contacts.Add(Contact(20002, "李四", "Lumia 950 XL", true));
        _contacts.Add(Contact(20003, "王五", "在线", true));
        _contacts.Add(Contact(20004, "老妈", "", false));
        _contacts.Add(Contact(20005, "老板", "勿扰", false));
        _contacts.Add(Contact(20006, "前端老哥", "CSS 是门玄学", false));

        Seed("c1", ("张三", "在吗？"), ("张三", "晚上一起吃饭"));
        Seed("c2", ("李四", "Lumia 950 永不为奴"), ("王五", "+1"));
        Seed("c3", ("老妈", "记得多穿点衣服"));
        Seed("c5", ("QQ 团队", "欢迎使用 QQ Reborn"), ("QQ 团队", "为 Windows 10 Mobile 打造的第三方 QQ 客户端"));

        _groupMembers["c2"] = new List<JsonObject>
        {
            Member(10001, "Jimmy", SelfAvatar, "群主"),
            Member(20002, "李四", FriendAvatar, "管理员"),
            Member(20003, "王五", FriendAvatar, ""),
            Member(20007, "WP老炮", FriendAvatar, ""),
            Member(20008, "Lumia930", FriendAvatar, ""),
            Member(20009, "诺基亚情怀", FriendAvatar, ""),
            Member(20010, "Surface党", FriendAvatar, ""),
        };
        _groupMembers["c4"] = new List<JsonObject>
        {
            Member(10001, "Jimmy", SelfAvatar, "群主"),
            Member(20004, "老妈", FriendAvatar, ""),
            Member(20011, "老爸", FriendAvatar, ""),
            Member(20012, "妹妹", FriendAvatar, ""),
        };

        _friendRequests.Add(Request(30001, "陈同学", "我是你大学同学，加一下"));
        _friendRequests.Add(Request(30002, "Lumia 复活会", "看你也是 WP 钉子户，交个朋友"));
        _friendRequests.Add(Request(30003, "代购小妹", "通过群聊添加"));
    }

    private string NextId() => "m" + Interlocked.Increment(ref _idSeed);

    public JsonObject GetSelf() => new()
    {
        ["uin"] = 10001,
        ["nickname"] = "Jimmy",
        ["avatarPath"] = SelfAvatar,
        ["signature"] = "WP 永不为奴",
    };

    public JsonArray GetConversations()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var c in _conversations.OrderByDescending(c => (string)c["lastTime"]!))
                arr.Add(Clone(c));
            return arr;
        }
    }

    public JsonArray GetContacts()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var c in _contacts) arr.Add(Clone(c));
            return arr;
        }
    }

    public JsonArray GetMessages(string convId)
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            if (_messages.TryGetValue(convId, out var list))
                foreach (var m in list) arr.Add(Clone(m));
            return arr;
        }
    }

    public JsonArray GetGroupMembers(string convId)
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            if (_groupMembers.TryGetValue(convId, out var list))
                foreach (var m in list) arr.Add(Clone(m));
            return arr;
        }
    }

    public JsonArray GetFriendRequests()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var r in _friendRequests) arr.Add(Clone(r));
            return arr;
        }
    }

    public JsonObject AcceptFriendRequest(long uin)
    {
        lock (_gate)
        {
            var req = _friendRequests.FirstOrDefault(r => (long)r["uin"]! == uin);
            if (req != null) req["handled"] = true;
            return new JsonObject { ["uin"] = uin, ["handled"] = true };
        }
    }

    public JsonObject Send(string convId, string contentType, string? text, string? imagePath, string? audioPath, int voiceSeconds,
        string? placeName = null, string? address = null, string? thumb = null)
    {
        var preview = contentType switch
        {
            "Image" => "[图片]",
            "Sticker" => "[表情]",
            "Voice" => "[语音]",
            "Location" => "[位置]",
            _ => text ?? "",
        };
        var msg = Message(convId, "我", 10001, SelfAvatar, "Outgoing", contentType, text ?? preview, imagePath, audioPath, voiceSeconds);
        if (placeName != null) msg["placeName"] = placeName;
        if (address != null) msg["address"] = address;
        if (thumb != null) msg["thumb"] = thumb;
        lock (_gate)
        {
            if (!_messages.TryGetValue(convId, out var list)) { list = new(); _messages[convId] = list; }
            list.Add(msg);
            BumpConversation(convId, preview);
        }
        RunSafe(AutoReplyAsync(convId));
        return Clone(msg);
    }

    /// <summary>Observe/log exceptions from a fire-and-forget task so they aren't silently lost.</summary>
    private static async void RunSafe(Task t)
    {
        try { await t; }
        catch (Exception ex) { Console.WriteLine("[!] " + ex); }
    }

    private async Task AutoReplyAsync(string convId)
    {
        await Task.Delay(500 + _rng.Next(400));
        PushTyping(convId, true);
        await Task.Delay(900 + _rng.Next(700));
        PushTyping(convId, false);

        string[] replies = { "收到~", "好的", "哈哈哈", "这边 QQ Reborn 测试中", "嗯嗯", "稍等" };
        string title;
        string avatar;
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == convId);
            title = conv != null ? (string)conv["title"]! : "对方";
            avatar = conv != null && (string)conv["kind"]! == "Group" ? GroupAvatar : FriendAvatar;
        }
        var reply = Message(convId, title, 20999, avatar, "Incoming", "Text", replies[_rng.Next(replies.Length)], null, null, 0);
        lock (_gate)
        {
            if (!_messages.TryGetValue(convId, out var list)) { list = new(); _messages[convId] = list; }
            list.Add(reply);
            BumpConversation(convId, (string)reply["text"]!);
        }

        var frame = new JsonObject { ["type"] = "messageReceived", ["data"] = Clone(reply) };
        Broadcast?.Invoke(frame.ToJsonString());
    }

    private void PushTyping(string convId, bool typing)
    {
        var frame = new JsonObject
        {
            ["type"] = "typing",
            ["data"] = new JsonObject { ["conversationId"] = convId, ["isTyping"] = typing },
        };
        Broadcast?.Invoke(frame.ToJsonString());
    }

    private void BumpConversation(string convId, string preview)
    {
        var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == convId);
        if (conv != null)
        {
            conv["preview"] = preview;
            conv["lastTime"] = DateTimeOffset.UtcNow.ToString("o");
        }
    }

    // ---- builders ----

    private JsonObject Conv(string id, string kind, string title, string avatar, string preview, int minsAgo, int unread) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["title"] = title,
        ["avatarPath"] = avatar,
        ["preview"] = preview,
        ["lastTime"] = DateTimeOffset.UtcNow.AddMinutes(minsAgo).ToString("o"),
        ["unread"] = unread,
    };

    private JsonObject Contact(long uin, string name, string sig, bool online) => new()
    {
        ["uin"] = uin,
        ["name"] = name,
        ["avatarPath"] = FriendAvatar,
        ["signature"] = sig,
        ["online"] = online,
    };

    private JsonObject Member(long uin, string name, string avatar, string role) => new()
    {
        ["uin"] = uin,
        ["name"] = name,
        ["avatarPath"] = avatar,
        ["role"] = role,
    };

    private JsonObject Request(long uin, string name, string message) => new()
    {
        ["uin"] = uin,
        ["name"] = name,
        ["avatarPath"] = FriendAvatar,
        ["message"] = message,
        ["handled"] = false,
    };

    private JsonObject Message(string convId, string sender, long uin, string avatar, string dir,
        string contentType, string text, string? imagePath, string? audioPath, int voiceSeconds) => new()
    {
        ["id"] = NextId(),
        ["conversationId"] = convId,
        ["senderName"] = sender,
        ["senderUin"] = uin,
        ["senderAvatarPath"] = avatar,
        ["direction"] = dir,
        ["contentType"] = contentType,
        ["text"] = text,
        ["imagePath"] = imagePath,
        ["audioPath"] = audioPath,
        ["voiceSeconds"] = voiceSeconds,
        ["time"] = DateTimeOffset.UtcNow.ToString("o"),
        ["state"] = "Sent",
    };

    private void Seed(string convId, params (string sender, string text)[] items)
    {
        var conv = _conversations.First(c => (string)c["id"]! == convId);
        var avatar = (string)conv["kind"]! == "Group" ? GroupAvatar : FriendAvatar;
        var list = new List<JsonObject>();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-items.Length - 2);
        for (int i = 0; i < items.Length; i++)
        {
            var m = Message(convId, items[i].sender, 20000 + i, avatar, "Incoming", "Text", items[i].text, null, null, 0);
            m["time"] = baseTime.AddMinutes(i).ToString("o");
            list.Add(m);
        }
        _messages[convId] = list;
    }

    private static JsonObject Clone(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
}
