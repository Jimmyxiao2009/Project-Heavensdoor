using System.Net.Http;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Events.EventArgs;
using Lagrange.Core.Exceptions;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using QQReborn.Signing;

namespace QQReborn.RealServer;

/// <summary>
/// Live LagrangeV2-backed bridge. Analogous to QQReborn.FakeServer's ChatState, but state
/// is fed from a real BotContext instead of seed data. Single-account (one BotContext at a
/// time) -- account config arrives at runtime from the phone via ConfigureAccountAsync,
/// there is no local config file (see server/README-less design: signUrl/signToken/signUin
/// are captured by the app's Settings page and forwarded over the WS connection).
/// </summary>
public class BotSessionManager : ISessionBackend
{
    public string BackendId => BackendFactory.Lagrange;

    private static readonly string KeystorePath = Path.Combine(AppContext.BaseDirectory, "keystore.json");

    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _messages = new();
    private readonly List<JsonObject> _conversations = new();
    private readonly List<JsonObject> _contacts = new();
    private readonly Dictionary<string, List<JsonObject>> _groupMembers = new();
    private readonly List<JsonObject> _friendRequests = new();

    /// <summary>Real BotMessage objects keyed by our wire id (see <see cref="WireMessageId"/>),
    /// needed because MessageBuilder.Reply(BotMessage) requires the actual source object
    /// (Sequence/Contact/MessageId), not just our JSON snapshot of it. Populated for both
    /// sent and received messages so replying to either direction works.</summary>
    private readonly Dictionary<string, BotMessage> _rawMessages = new();

    /// <summary>Friend (C2C) send/echo pairing: "{convId}:cs:{clientSequence}" -> recorded wire id.
    /// Needed because LagrangeV2's C2C send response carries the CLIENT sequence (a random
    /// 5-digit number) while Tencent's multi-device echo carries the SERVER sequence -- the
    /// two ids never match, so without this index every sent DM would be recorded twice.
    /// The echo does carry the same ClientSequence, which is the only reliable join key.</summary>
    private readonly Dictionary<string, string> _clientSeqToId = new();

    /// <summary>Session-level getUserProfile cache: uin -> (wire data, expiry). Avoids hammering
    /// Tencent's FetchStranger endpoint when the client re-opens the same contact-detail page
    /// repeatedly. Cleared implicitly on every fresh ConfigureAccountAsync (new Dictionary below
    /// replaces this one's contents via .Clear() alongside the other per-session state).</summary>
    private readonly Dictionary<long, (JsonObject data, DateTime expiry)> _profileCache = new();
    private static readonly TimeSpan ProfileCacheTtl = TimeSpan.FromMinutes(5);

    private BotContext? _bot;
    private TokenSignProvider? _signProvider;
    private bool _online;
    private bool _loginInFlight;

    /// <summary>Cached from a background FetchStranger(bot.BotUin) fired once on OnOnline, since
    /// BotContext itself doesn't surface the logged-in account's own signature/level anywhere
    /// cheaper. Both stay at their defaults ("" / 0) until that fetch completes.</summary>
    private string _selfSignature = "";
    private int _selfLevel;

    /// <summary>Last qrCode push frame (JSON string), kept so a client that re-subscribes
    /// mid-login (navigated away and back) can be replayed the QR it missed -- BotQrCodeEvent
    /// only fires once per login attempt. Cleared when the attempt ends either way.</summary>
    private string? _lastQrFrame;

    /// <summary>Per-conversation pin/mute flags. LagrangeV2 has no public API to sync these
    /// with Tencent, so we keep them on the bridge (persisted under the logged-in uin) and
    /// the App treats them like the phone QQ 置顶 / 消息免打扰 toggles.</summary>
    private readonly Dictionary<string, (bool pinned, bool muted)> _convPrefs = new();
    private long _prefsUin;

    /// <summary>Conversation ids we've already attempted a cloud history backfill for this
    /// session. Prevents open-chat storms (swipe through many groups) from hammering the
    /// public sign server into HTTP 401 and then breaking every subsequent send.</summary>
    private readonly HashSet<string> _historyPullAttempted = new();
    private Task? _populateTask;

    /// <summary>QQ Space / 动态 feed injected via HTTP webhook (web 空间/第三方推送)
    /// and natively fetched from QQ Zone HTTP API (via pskey auth).
    /// Merged and deduped by feed id; max <see cref="MaxSpaceFeedItems"/> items.</summary>
    private readonly List<JsonObject> _spaceFeed = new();
    private const int MaxSpaceFeedItems = 200;
    private int _spaceFeedPos;          // last fetched position for native pagination
    private bool _spaceFeedHasMore = true; // true while QQ Zone API reports more pages
    private DateTime _spaceFeedLastFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan SpaceFeedMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>Raised to broadcast an event frame (already a JSON string) to every client.</summary>
    public event Action<string>? Broadcast;

    // ---- account lifecycle ----

    public async Task<(JsonObject? data, string? error)> ConfigureAccountAsync(string signUrl, string? signToken, string signUinRaw)
    {
        if (!long.TryParse(signUinRaw, out var uin) || uin <= 0)
            return (null, "invalid-uin：QQ号必须是纯数字，请检查设置里的QQ号");

        bool reentrant = false;
        string? replayQr = null;
        lock (_gate)
        {
            if (_online) return (null, "already-online");
            if (_loginInFlight)
            {
                // A login attempt is already in flight and owns _bot/_signProvider/the state
                // dictionaries right now -- touching any of them here (e.g. falling through to
                // the Dispose()/BotFactory.Create() below) would rip the rug out from under it.
                // This isn't limited to the QR-code flow: a keystore fast-login never produces
                // a QR at all, and even a QR-code login has a window before BotQrCodeEvent fires
                // where _lastQrFrame is still null. In every one of those cases the right move
                // is the same -- leave the in-flight attempt alone and just ack the request,
                // replaying whatever QR/status we do have so a client that re-entered the login
                // page isn't stuck looking at nothing.
                reentrant = true;
                replayQr = _lastQrFrame;
            }
            else
            {
                _loginInFlight = true;
                // Fresh attempt: drop everything accumulated by the previous account/session.
                // Without this, switching accounts merges the old account's conversations,
                // transcripts and contacts into the new session (and PopulateConversationsAsync
                // skips existing ids, so the stale rows would never be repaired).
                _messages.Clear();
                _conversations.Clear();
                _contacts.Clear();
                _groupMembers.Clear();
                _friendRequests.Clear();
                _rawMessages.Clear();
                _clientSeqToId.Clear();
                _profileCache.Clear();
                _selfSignature = "";
                _selfLevel = 0;
                _lastQrFrame = null;
                _historyPullAttempted.Clear();
                // Load this account's pin/mute prefs now so rows created during
                // PopulateConversationsAsync already carry the right flags.
                LoadPrefs(uin);
            }
        }

        if (reentrant)
        {
            // If we have a cached QR frame, replay it. Either way, nudge the client with a
            // waitingForScan status so it isn't stuck showing nothing -- this also covers
            // keystore fast-login and the pre-BotQrCodeEvent window, neither of which ever
            // produces a QR frame to replay.
            if (replayQr != null) Broadcast?.Invoke(replayQr);
            BroadcastLoginStatus("waitingForScan", 0, null);
            return (new JsonObject { ["accepted"] = true }, null);
        }

        try
        {
            // The previous attempt's BotContext (if any) is dead weight now -- it holds a live
            // socket, a 2s heartbeat timer and 11 event handlers wired into our Broadcast, and
            // BotContext implements IDisposable. Leaking one per retry adds up fast.
            _bot?.Dispose();
            _bot = null;
            _signProvider?.Dispose();

            Console.WriteLine($"[ConfigureAccount] signUrl={signUrl} token={(string.IsNullOrEmpty(signToken) ? "none" : $"present({signToken.Length})") } uin={uin}");
            var sign = new TokenSignProvider(signUrl, signToken, uin);
            _signProvider = sign;
            var config = new BotConfig
            {
                Protocol = Protocols.Linux,
                SignProvider = sign,
                AutoReconnect = true,
                AutoReLogin = true,
                GetOptimumServer = true,
                UseIPv6Network = false,
                LogLevel = Lagrange.Core.Events.EventArgs.LogLevel.Information,
            };

            BotKeystore? keystore = null;
            if (File.Exists(KeystorePath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<BotKeystore>(await File.ReadAllTextAsync(KeystorePath));
                    if (loaded != null && (uin == 0 || loaded.Uin == uin)) keystore = loaded;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[!] keystore.json unreadable, starting fresh: " + ex.Message);
                }
            }

            var bot = keystore is null ? BotFactory.Create(config) : BotFactory.Create(config, keystore);
            _bot = bot;
            WireEvents(bot);

            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine("[Login] starting bot.Login() ...");
                    var ok = await bot.Login();
                    Console.WriteLine($"[Login] bot.Login() returned ok={ok}");
                    if (!ok) BroadcastLoginStatus("failed", 0, "登录失败（未收到更具体的原因，检查签名token是否有效/是否已绑定该QQ号）");
                }
                catch (Exception ex)
                {
                    // Surface the full chain -- Lagrange wraps the real fault as
                    // "An error occurred while sending the event" and hides the cause
                    // (e.g. HttpRequestException from the sign server) in InnerException.
                    var detail = ex.Message;
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        detail += " → " + inner.GetType().Name + ": " + inner.Message;
                    Console.WriteLine("[Login] EXCEPTION " + ex);
                    BroadcastLoginStatus("failed", 0, detail);
                }
                finally
                {
                    lock (_gate) _loginInFlight = false;
                }
            });
        }
        catch
        {
            // Setup blew up before the background Task.Run even started (Dispose/BotFactory.Create
            // throwing) -- nothing is going to clear _loginInFlight for us, so it would stay
            // stuck forever, permanently blocking every future ConfigureAccountAsync call.
            lock (_gate) _loginInFlight = false;
            throw;
        }

        return (new JsonObject { ["accepted"] = true }, null);
    }

    private void WireEvents(BotContext bot)
    {
        bot.EventInvoker.RegisterEvent<BotLogEvent>(OnLog);
        bot.EventInvoker.RegisterEvent<BotQrCodeEvent>(OnQrCode);
        bot.EventInvoker.RegisterEvent<BotQrCodeQueryEvent>(OnQrCodeQuery);
        bot.EventInvoker.RegisterEvent<BotRefreshKeystoreEvent>(OnRefreshKeystore);
        bot.EventInvoker.RegisterEvent<BotOnlineEvent>(OnOnline);
        bot.EventInvoker.RegisterEvent<BotOfflineEvent>(OnOffline);
        bot.EventInvoker.RegisterEvent<BotMessageEvent>(OnBotMessage);
        bot.EventInvoker.RegisterEvent<BotFriendRequestEvent>(OnFriendRequest);
        bot.EventInvoker.RegisterEvent<BotCaptchaEvent>(OnCaptcha);
        bot.EventInvoker.RegisterEvent<BotNewDeviceVerifyEvent>(OnNewDeviceVerify);
        bot.EventInvoker.RegisterEvent<BotSMSEvent>(OnSms);
    }

    private void OnLog(BotContext ctx, BotLogEvent e) => Console.WriteLine($"[LOG/{e.Level}] [{e.Tag}] {e.Message}");

    private void OnQrCode(BotContext ctx, BotQrCodeEvent e)
    {
        var b64 = Convert.ToBase64String(e.Image);
        var frame = new JsonObject
        {
            ["type"] = "qrCode",
            ["data"] = new JsonObject { ["url"] = e.Url, ["imageBase64"] = b64 },
        }.ToJsonString();
        lock (_gate) _lastQrFrame = frame;
        Broadcast?.Invoke(frame);
        BroadcastLoginStatus("waitingForScan", 0, null);
    }

    private void OnQrCodeQuery(BotContext ctx, BotQrCodeQueryEvent e)
    {
        var state = e.State switch
        {
            BotQrCodeQueryEvent.TransEmpState.WaitingForScan => "waitingForScan",
            BotQrCodeQueryEvent.TransEmpState.WaitingForConfirm => "waitingForConfirm",
            BotQrCodeQueryEvent.TransEmpState.CodeExpired => "expired",
            BotQrCodeQueryEvent.TransEmpState.Canceled => "canceled",
            BotQrCodeQueryEvent.TransEmpState.Invalid => "failed",
            _ => null, // Confirmed -> no push, BotOnlineEvent follows momentarily with the authoritative "online"
        };
        // Terminal states: the QR is dead, stop replaying it to late subscribers.
        if (state is "expired" or "canceled" or "failed")
            lock (_gate) _lastQrFrame = null;
        if (state != null) BroadcastLoginStatus(state, 0, null);
    }

    private async Task OnRefreshKeystore(BotContext ctx, BotRefreshKeystoreEvent e)
    {
        try { await File.WriteAllTextAsync(KeystorePath, JsonSerializer.Serialize(e.Keystore)); }
        catch (Exception ex) { Console.WriteLine("[!] keystore save failed: " + ex.Message); }
    }

    private void OnOnline(BotContext ctx, BotOnlineEvent e)
    {
        lock (_gate)
        {
            _online = true;
            _lastQrFrame = null;
            // Keystore fast-login may surface a uin different from the one the client typed;
            // re-load prefs under the authoritative online uin so flags don't go missing.
            if (_prefsUin != ctx.BotUin) LoadPrefs(ctx.BotUin);
        }
        BroadcastLoginStatus("online", ctx.BotUin, null);
        _ = EnsureConversationsPopulatedAsync(ctx);
        _ = Task.Run(async () =>
        {
            // Give conversations/contacts a moment to populate first, then pull space feeds.
            await Task.Delay(2000);
            try { await FetchQzoneFeedNativeAsync(); }
            catch (Exception ex) { Console.WriteLine("[!] auto space fetch on login: " + ex.Message); }
        });
    }

    /// <summary>Fire-and-forget: LagrangeV2 never surfaces the logged-in account's own
    /// signature/level anywhere cheaper than the same FetchStranger call used for other
    /// users' profiles, so grab it once after login and cache it for GetSelf(). Best-effort --
    /// GetSelf() just keeps returning the "" / 0 defaults if this fails or hasn't landed yet.</summary>
    private async Task FetchSelfProfileAsync(BotContext ctx)
    {
        try
        {
            var stranger = await ctx.FetchStranger(ctx.BotUin);
            lock (_gate)
            {
                _selfSignature = stranger.PersonalSign ?? "";
                _selfLevel = (int)stranger.Level;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] FetchSelfProfileAsync failed: " + ex.Message);
        }
    }

    private void OnOffline(BotContext ctx, BotOfflineEvent e)
    {
        lock (_gate) _online = false;
        BroadcastLoginStatus("offline", 0, e.Tips?.Message);
    }

    private void OnCaptcha(BotContext ctx, BotCaptchaEvent e)
        => BroadcastLoginStatus("failed", 0, "需要验证码，暂不支持这个流程");

    private void OnNewDeviceVerify(BotContext ctx, BotNewDeviceVerifyEvent e)
        => BroadcastLoginStatus("failed", 0, "需要新设备验证，暂不支持这个流程");

    private void OnSms(BotContext ctx, BotSMSEvent e)
        => BroadcastLoginStatus("failed", 0, "需要短信验证，暂不支持这个流程");

    private void OnFriendRequest(BotContext ctx, BotFriendRequestEvent e)
    {
        lock (_gate)
        {
            _friendRequests.Add(new JsonObject
            {
                ["uin"] = e.InitiatorUin,
                ["name"] = e.InitiatorUin.ToString(),
                ["avatarPath"] = FriendAvatarUrl(e.InitiatorUin),
                ["message"] = e.Message,
                ["handled"] = false,
            });
        }
    }

    private void OnBotMessage(BotContext ctx, BotMessageEvent e)
    {
        var msg = e.Message;

        // Tencent echoes our own C2C (friend) sends back to us for multi-device sync.
        // msg.Contact for that echo is NOT a real BotFriend -- LagrangeV2's friend-list
        // lookup for "yourself as a friend" always misses (you can't be your own friend,
        // same root cause as the SendFriendMessage bug patched in Lagrange.Core), so it
        // falls back to a garbage placeholder (Uin=self, Nickname=your own internal Uid
        // string). Route by msg.Receiver (the real other party) instead in that case.
        // Group self-echoes don't need this: msg.Contact there is already a proper
        // BotGroupMember tied to the correct Group (you ARE a real member of your own groups).
        var isFriendSelfEcho = !(msg.Contact is BotGroupMember) && msg.Contact.Uin == ctx.BotUin;
        // Own sends synced back from another device also happen in groups; there the Contact
        // is a proper BotGroupMember (you ARE a member of your own groups) so routing is fine,
        // but the message is still OURS and must render as outgoing, not as an incoming
        // bubble from ourselves.
        var isSelf = msg.Contact.Uin == ctx.BotUin;

        string convId, senderName, convTitle, convAvatar;
        long senderUin;

        if (isFriendSelfEcho)
        {
            convId = $"f{msg.Receiver.Uin}";
            senderName = ctx.BotInfo?.Name ?? ctx.BotUin.ToString();
            senderUin = ctx.BotUin;
            // The conversation this belongs to is the RECEIVER's -- naming it after the
            // sender (ourselves) would create/label the peer's conversation with our own
            // name and avatar when it doesn't exist yet.
            convTitle = string.IsNullOrEmpty(msg.Receiver.Nickname) ? msg.Receiver.Uin.ToString() : msg.Receiver.Nickname;
            convAvatar = FriendAvatarUrl(msg.Receiver.Uin);
        }
        else
        {
            convId = msg.Contact is BotGroupMember gm ? $"g{gm.Group.GroupUin}" : $"f{msg.Contact.Uin}";
            senderName = msg.Contact is BotGroupMember member
                ? (string.IsNullOrEmpty(member.MemberCard) ? member.Nickname : member.MemberCard)
                : msg.Contact.Nickname;
            senderUin = msg.Contact.Uin;
            convTitle = msg.Contact is BotGroupMember gm2 ? gm2.Group.GroupName : senderName;
            convAvatar = msg.Contact is BotGroupMember gm3 ? GroupAvatarUrl(gm3.Group.GroupUin) : FriendAvatarUrl(senderUin);
        }
        var direction = isSelf ? "Outgoing" : "Incoming";

        // msg.Sequence here is always the real SERVER sequence (this is a message fresh off
        // the wire, never routed through SendFriendMessage's client-sequence overwrite -- see
        // WireMessageId for why C2C needs a distinct prefix from the one SendAsync uses).
        var msgId = WireMessageId(convId, msg.Sequence, isServerSequence: true);
        var (contentType, text, imagePath, audioPath, voiceSeconds, replyToSender, replyToText) = MapEntities(msg.Entities, convId);

        var wire = new JsonObject
        {
            ["id"] = msgId,
            ["conversationId"] = convId,
            ["conversationTitle"] = convTitle,
            ["conversationAvatarPath"] = convAvatar,
            ["senderName"] = senderName,
            ["senderUin"] = senderUin,
            ["senderAvatarPath"] = FriendAvatarUrl(senderUin),
            ["direction"] = direction,
            ["contentType"] = contentType,
            ["text"] = text,
            ["imagePath"] = imagePath,
            ["audioPath"] = audioPath,
            ["voiceSeconds"] = voiceSeconds,
            ["elements"] = BuildElements(msg.Entities),
            ["time"] = new DateTimeOffset(DateTime.SpecifyKind(msg.Time, DateTimeKind.Utc)).ToString("o"),
            ["state"] = "Sent",
        };
        if (!string.IsNullOrEmpty(replyToSender)) wire["replyToSender"] = replyToSender;
        if (!string.IsNullOrEmpty(replyToText)) wire["replyToText"] = replyToText;
        // Group file download needs FileId (not the wire message id).
        if (contentType is "FileMsg" or "File")
        {
            var gf = msg.Entities.OfType<GroupFileEntity>().FirstOrDefault();
            if (gf != null)
            {
                wire["fileId"] = gf.FileId;
                wire["fileName"] = gf.FileName;
                wire["fileSize"] = FormatFileSize(gf.FileSize);
                Console.WriteLine($"[FileDL] incoming file: id={gf.FileId[..Math.Min(30, gf.FileId.Length)]} name={gf.FileName}");
            }
            else Console.WriteLine($"[FileDL] FileMsg but no GroupFileEntity! entities={msg.Entities.Count} types=[{string.Join(",", msg.Entities.Select(e => e.GetType().Name))}]");
        }

        // Single critical section for check + insert: the old split (check in one lock,
        // insert in a later one) raced SendAsync's post-await insert and could record the
        // same message twice.
        var clientKey = isFriendSelfEcho && msg.ClientSequence != 0 ? $"{convId}:cs:{msg.ClientSequence}" : null;
        lock (_gate)
        {
            if (!_messages.TryGetValue(convId, out var list)) { list = new(); _messages[convId] = list; }

            // C2C echo of a DM WE sent through this bridge: the send response was recorded
            // under the random CLIENT sequence, this echo carries the SERVER sequence -- the
            // ids never match, so pair them via ClientSequence instead. Refresh the raw
            // message so later reply-quotes embed the server sequence (the one the peer's
            // client can actually resolve), then drop the echo.
            if (clientKey != null && _clientSeqToId.TryGetValue(clientKey, out var recordedId))
            {
                _rawMessages[recordedId] = msg;
                return;
            }
            if (_rawMessages.ContainsKey(msgId)) return; // already recorded (send response beat us)

            list.Add(wire);
            _rawMessages[msgId] = msg;
            if (clientKey != null) _clientSeqToId[clientKey] = msgId;
        }

        var kind = convId[0] == 'g' ? "Group" : "Friend";
        // Incoming from others → bump unread unless this conversation is muted. Own/outgoing
        // echoes only refresh preview. Server-side filtering keeps reconnects and fresh list
        // snapshots from resurrecting an unread badge.
        var muted = false;
        lock (_gate) muted = _convPrefs.TryGetValue(convId, out var pref) && pref.muted;
        BumpConversationOrCreate(convId, kind, convTitle, convAvatar, PreviewFor(contentType, text),
            incrementUnread: !isSelf && !muted);

        Broadcast?.Invoke(new JsonObject { ["type"] = "messageReceived", ["data"] = Clone(wire) }.ToJsonString());
    }

    private void BroadcastLoginStatus(string state, long uin, string? message)
    {
        var frame = new JsonObject
        {
            ["type"] = "loginStatus",
            ["data"] = new JsonObject { ["state"] = state, ["uin"] = uin, ["message"] = message },
        };
        Broadcast?.Invoke(frame.ToJsonString());
    }

    // ---- conversation population ----

    private async Task EnsureConversationsPopulatedAsync(BotContext bot)
    {
        lock (_gate)
        {
            if (_populateTask != null && !_populateTask.IsCompleted) return;
            _populateTask = PopulateConversationsWithRetryAsync(bot);
        }
        await _populateTask;
    }

    private async Task PopulateConversationsWithRetryAsync(BotContext bot)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await PopulateConversationsAsync(bot);
            lock (_gate)
            {
                if (_contacts.Count > 0 || _conversations.Count > 0) return;
            }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
        }
    }

    private async Task PopulateConversationsAsync(BotContext bot)
    {
        try
        {
            var friends = await bot.FetchFriends(refresh: true);
            var groups = await bot.FetchGroups(refresh: true);

            lock (_gate)
            {
                foreach (var f in friends)
                {
                    var id = $"f{f.Uin}";
                    if (_conversations.Any(c => (string)c["id"]! == id)) continue;
                    var title = string.IsNullOrEmpty(f.Remarks) ? f.Nickname : f.Remarks;
                    var row = new JsonObject
                    {
                        ["id"] = id,
                        ["kind"] = "Friend",
                        ["title"] = title,
                        ["avatarPath"] = FriendAvatarUrl(f.Uin),
                        ["preview"] = "",
                        ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["unread"] = 0,
                    };
                    ApplyPrefsTo(row);
                    _conversations.Add(row);
                    if (!_contacts.Any(c => (long)c["uin"]! == f.Uin))
                    {
                        _contacts.Add(new JsonObject
                        {
                            ["uin"] = f.Uin,
                            ["name"] = title,
                            ["avatarPath"] = FriendAvatarUrl(f.Uin),
                            ["signature"] = f.PersonalSign,
                            ["online"] = true,
                        });
                    }
                }

                foreach (var g in groups)
                {
                    var id = $"g{g.GroupUin}";
                    if (_conversations.Any(c => (string)c["id"]! == id)) continue;
                    var row = new JsonObject
                    {
                        ["id"] = id,
                        ["kind"] = "Group",
                        ["title"] = g.GroupName,
                        ["avatarPath"] = GroupAvatarUrl(g.GroupUin),
                        ["preview"] = "",
                        ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["unread"] = 0,
                        ["announcement"] = g.Announcement,
                    };
                    ApplyPrefsTo(row);
                    _conversations.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] PopulateConversationsAsync failed: " + ex.Message);
        }
    }

    // ---- queries (called from Program.cs's request dispatch) ----

    public JsonObject GetSelf()
    {
        var bot = _bot;
        string signature;
        int level;
        lock (_gate) { signature = _selfSignature; level = _selfLevel; }
        return new JsonObject
        {
            ["uin"] = bot?.BotUin ?? 0,
            ["nickname"] = bot?.BotInfo?.Name ?? "",
            ["avatarPath"] = bot != null ? FriendAvatarUrl(bot.BotUin) : "",
            ["signature"] = signature,
            ["level"] = level,
        };
    }

    public JsonArray GetConversations()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            // Pinned first (phone QQ 置顶), then most-recent activity.
            foreach (var c in _conversations
                .OrderByDescending(c => IsTruthy(c, "isPinned"))
                .ThenByDescending(c => (string)c["lastTime"]!))
                arr.Add(Clone(c));
            return arr;
        }
    }

    /// <summary>Update pin and/or mute for a conversation. Null means "leave this flag alone".
    /// Unknown ids still get a prefs entry so a later PopulateConversationsAsync/Bump that
    /// creates the row will pick the flags up; the response always reports the final values.</summary>
    public JsonObject SetConversationFlags(string conversationId, bool? isPinned, bool? isMuted)
    {
        if (string.IsNullOrEmpty(conversationId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-conversation" };
        if (isPinned == null && isMuted == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "no-flags" };

        lock (_gate)
        {
            // Missing entry defaults to (false, false) -- correct for first-time toggles.
            _convPrefs.TryGetValue(conversationId, out var prev);
            var pinned = isPinned ?? prev.pinned;
            var muted = isMuted ?? prev.muted;
            _convPrefs[conversationId] = (pinned, muted);

            var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == conversationId);
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

    public async Task<JsonArray> GetContactsAsync()
    {
        var bot = _bot;
        if (bot != null)
        {
            await EnsureConversationsPopulatedAsync(bot);
        }
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var c in _contacts) arr.Add(Clone(c));
            return arr;
        }
    }

    /// <summary>Return the in-memory transcript for a conversation.
    /// When <paramref name="allowCloudBackfill"/> is true and the cache is empty, pull one
    /// page of cloud history once (per conversation per session) so opening a chat after
    /// login isn't blank. Search must pass false — otherwise scanning every conversation
    /// stampedes the sign server and freezes the UI for minutes.</summary>
    public async Task<JsonArray> GetMessagesAsync(string conversationId, bool allowCloudBackfill = true)
    {
        int localCount;
        bool shouldPull;
        lock (_gate)
        {
            localCount = _messages.TryGetValue(conversationId, out var list) ? list.Count : 0;
            // Only auto-pull once when truly empty. Further history is via getEarlierMessages.
            shouldPull = allowCloudBackfill && localCount == 0 && _historyPullAttempted.Add(conversationId);
        }

        if (shouldPull)
        {
            try
            {
                Console.WriteLine($"[GetMessages] empty cache for {conversationId}, one-shot cloud pull…");
                await GetEarlierMessagesAsync(conversationId, beforeId: null, count: 20);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] GetMessages cloud backfill failed: {ex}");
            }
        }

        lock (_gate)
        {
            // Opening a chat (allowCloudBackfill=true) marks it read. Search uses
            // allowCloudBackfill=false and must NOT clear badges just for scanning.
            if (allowCloudBackfill)
                ClearUnreadLocked(conversationId);

            var arr = new JsonArray();
            if (_messages.TryGetValue(conversationId, out var list))
                foreach (var m in list) arr.Add(Clone(m));
            return arr;
        }
    }

    /// <summary>Client is looking at this conversation (live push or re-open). Clear badge.</summary>
    public JsonObject MarkConversationRead(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-conversation" };
        lock (_gate) { ClearUnreadLocked(conversationId); }
        return new JsonObject { ["ok"] = true, ["conversationId"] = conversationId, ["unread"] = 0 };
    }

    /// <summary>Caller must hold _gate.</summary>
    private void ClearUnreadLocked(string conversationId)
    {
        var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == conversationId);
        if (conv != null) conv["unread"] = 0;
    }

    public async Task<JsonArray> GetGroupMembersAsync(string conversationId)
    {
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return new JsonArray();

        List<JsonObject>? cached;
        lock (_gate) { _groupMembers.TryGetValue(conversationId, out cached); }

        List<JsonObject> members;
        if (cached != null)
        {
            members = cached;
        }
        else
        {
            var bot = _bot;
            if (bot == null) return new JsonArray();
            var fetched = await bot.FetchMembers(groupUin);
            members = fetched.Select(m => new JsonObject
            {
                ["uin"] = m.Uin,
                ["name"] = string.IsNullOrEmpty(m.MemberCard) ? m.Nickname : m.MemberCard,
                ["avatarPath"] = FriendAvatarUrl(m.Uin),
                ["role"] = m.Permission switch
                {
                    GroupMemberPermission.Owner => "群主",
                    GroupMemberPermission.Admin => "管理员",
                    _ => "",
                },
            }).ToList();
            lock (_gate) { _groupMembers[conversationId] = members; }
        }

        var arr = new JsonArray();
        foreach (var m in members) arr.Add(Clone(m));
        return arr;
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

    /// <summary>
    /// LagrangeV2 has no public API to accept/reject a friend request (confirmed absent --
    /// BotFriendRequestEvent can be observed but not acted on). Report this honestly instead
    /// of pretending it worked.
    /// </summary>
    public JsonObject AcceptFriendRequest(long uin)
        => new() { ["uin"] = uin, ["handled"] = false, ["reason"] = "unsupported" };

    /// <summary>Fetches an arbitrary user's public profile via FetchStranger, session-cached for
    /// ProfileCacheTtl so repeatedly opening the same contact-detail page doesn't hammer Tencent.</summary>
    public async Task<(JsonObject? data, string? error)> GetUserProfileAsync(long uin)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (uin <= 0) return (null, "invalid-uin");

        lock (_gate)
        {
            if (_profileCache.TryGetValue(uin, out var cached) && cached.expiry > DateTime.UtcNow)
                return (Clone(cached.data), null);
        }

        try
        {
            var stranger = await bot.FetchStranger(uin);
            var gender = stranger.Gender switch
            {
                BotGender.Male => "male",
                BotGender.Female => "female",
                _ => null,
            };
            var data = new JsonObject
            {
                ["uin"] = stranger.Uin,
                ["nickname"] = stranger.Nickname,
                ["signature"] = string.IsNullOrEmpty(stranger.PersonalSign) ? null : stranger.PersonalSign,
                ["level"] = (int)stranger.Level,
                ["gender"] = gender,
                ["age"] = (int)stranger.Age,
                ["country"] = string.IsNullOrEmpty(stranger.Country) ? null : stranger.Country,
                ["city"] = string.IsNullOrEmpty(stranger.City) ? null : stranger.City,
            };
            lock (_gate) { _profileCache[uin] = (data, DateTime.UtcNow + ProfileCacheTtl); }
            return (Clone(data), null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GetUserProfileAsync({uin}) failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Pages in older messages from the cloud (infinite-scroll-up / empty-chat
    /// backfill).
    /// <list type="bullet">
    /// <item>Friend (f): <c>GetRoamMessage</c> (time-anchored, walks backward).</item>
    /// <item>Group (g): <c>GetGroupMessage</c> over a sequence window; without a local
    ///   anchor we resolve the latest seq via <c>FetchGroupExtra</c> (same as Lagrange.Milky).</item>
    /// </list>
    /// <paramref name="beforeId"/> may be null/empty to mean "newest page from cloud"
    /// (used when the session has no messages yet). Otherwise pages older than that wire id.
    /// Results are de-duplicated against <see cref="_rawMessages"/> and spliced into the FRONT
    /// of the conversation's in-memory list so later <c>getMessages</c> sees them too.</summary>
    public async Task<(JsonObject? data, string? error)> GetEarlierMessagesAsync(string conversationId, string? beforeId, int count)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        var kind = conversationId.Length >= 2 ? conversationId[0] : '\0';
        if ((kind != 'f' && kind != 'g') || !long.TryParse(conversationId.AsSpan(1), out var peerUin) || peerUin <= 0)
            return (null, "invalid-conversation");

        // SsoGetRoamMsg Count is documented max 30; group ranges work with more but keep one
        // consistent page size for the UI.
        var clampedCount = Math.Clamp(count <= 0 ? 20 : count, 1, 30);

        BotMessage? anchor = null;
        if (!string.IsNullOrEmpty(beforeId))
        {
            lock (_gate) { _rawMessages.TryGetValue(beforeId, out anchor); }
            if (anchor == null)
            {
                // Client sent an id we don't know (e.g. after RealServer restart while the app
                // still holds old wire ids). Fall through to an unanchored "latest page" pull
                // rather than hard-failing -- better a re-sync than a stuck "unknown-message".
                Console.WriteLine($"[GetEarlier] beforeId={beforeId} not in _rawMessages; falling back to latest page");
            }
        }

        // When paging older and we already have local messages but no raw anchor, use the
        // oldest in-memory message's timestamp/sequence so we don't re-pull the same latest page.
        if (anchor == null && !string.IsNullOrEmpty(beforeId))
        {
            // already logged above
        }
        else if (anchor == null)
        {
            lock (_gate)
            {
                if (_messages.TryGetValue(conversationId, out var existing) && existing.Count > 0)
                {
                    // Prefer the oldest raw BotMessage we still hold so the next page is older.
                    for (int i = 0; i < existing.Count; i++)
                    {
                        var id = (string?)existing[i]["id"];
                        if (id != null && _rawMessages.TryGetValue(id, out var raw))
                        {
                            anchor = raw;
                            Console.WriteLine($"[GetEarlier] using oldest local raw msg {id} as soft-anchor");
                            break;
                        }
                    }
                }
            }
        }

        try
        {
            List<BotMessage> fetched;
            if (kind == 'f')
            {
                // GetRoamMessage requires peer UID in CacheContext (filled by FetchFriends).
                // Opening a chat right after login can race PopulateConversationsAsync --
                // force a friend-list resolve first so InvalidTargetException doesn't kill roam.
                await EnsureFriendUidAsync(bot, peerUin);

                // Roam walks from Time with Direction=2 (Lagrange default). Use the anchor's
                // time when paging older; for a brand-new open use "now". When re-paging the
                // same second, step back 1s so we don't keep getting the identical page.
                uint timestamp;
                if (anchor != null)
                {
                    var t = (uint)new DateTimeOffset(DateTime.SpecifyKind(anchor.Time, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    timestamp = t > 0 ? t - 1 : t; // strictly older than the on-screen oldest
                }
                else
                {
                    timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                Console.WriteLine($"[GetEarlier] friend roam peer={peerUin} time={timestamp} count={clampedCount} softAnchor={(anchor != null)}");
                fetched = await bot.GetRoamMessage(peerUin, timestamp, (uint)clampedCount);
            }
            else
            {
                ulong end;
                if (anchor != null && anchor.Sequence > 0)
                {
                    end = anchor.Sequence > 1 ? anchor.Sequence - 1 : 0;
                }
                else
                {
                    // No local anchor: ask Tencent for the group's latest sequence, then walk
                    // back `count` messages (same idea as Lagrange.Milky get_history_messages).
                    var extra = await bot.FetchGroupExtra(peerUin);
                    end = extra.LatestMessageSequence > 0 ? (ulong)extra.LatestMessageSequence : 0;
                    Console.WriteLine($"[GetEarlier] group={peerUin} FetchGroupExtra latestSeq={end}");
                }

                if (end == 0)
                {
                    fetched = new List<BotMessage>();
                    Console.WriteLine($"[GetEarlier] group={peerUin} no sequence available, empty");
                }
                else
                {
                    var start = end > (ulong)clampedCount ? end - (ulong)clampedCount + 1 : 1ul;
                    Console.WriteLine($"[GetEarlier] group range {start}..{end}");
                    fetched = start <= end
                        ? await bot.GetGroupMessage(peerUin, start, end)
                        : new List<BotMessage>();
                }
            }

            Console.WriteLine($"[GetEarlier] fetched {fetched.Count} raw message(s) for {conversationId}");

            // Oldest-first for the wire contract.
            fetched = fetched.OrderBy(m => m.Time).ThenBy(m => m.Sequence).ToList();

            // Drop the soft-anchor itself if the cloud page re-includes it.
            if (anchor != null)
                fetched = fetched.Where(m => !(m.Sequence == anchor.Sequence && m.Time == anchor.Time)).ToList();

            var wireMessages = new List<JsonObject>();
            lock (_gate)
            {
                if (!_messages.TryGetValue(conversationId, out var list)) { list = new(); _messages[conversationId] = list; }

                foreach (var msg in fetched)
                {
                    var msgId = WireMessageId(conversationId, msg.Sequence, isServerSequence: true);
                    if (_rawMessages.ContainsKey(msgId)) continue; // already known

                    var isSelf = msg.Contact.Uin == bot.BotUin;
                    // Friend self-echo / roam: Contact for our own messages is us; for the peer
                    // it's them. Group: Contact is BotGroupMember with card/name.
                    var senderName = msg.Contact is BotGroupMember gm
                        ? (string.IsNullOrEmpty(gm.MemberCard) ? gm.Nickname : gm.MemberCard)
                        : (isSelf ? (bot.BotInfo?.Name ?? bot.BotUin.ToString()) : msg.Contact.Nickname);
                    var senderUin = msg.Contact.Uin;
                    var isGroup = conversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase);
                    var convTitle = isGroup && msg.Contact is BotGroupMember groupMember
                        ? groupMember.Group.GroupName
                        : senderName;
                    var convAvatar = isGroup && msg.Contact is BotGroupMember groupMember2
                        ? GroupAvatarUrl(groupMember2.Group.GroupUin)
                        : FriendAvatarUrl(senderUin);
                    var (contentType, text, imagePath, audioPath, voiceSeconds, replyToSender, replyToText) = MapEntities(msg.Entities, conversationId);

                    var wire = new JsonObject
                    {
                        ["id"] = msgId,
                        ["conversationId"] = conversationId,
                        ["conversationTitle"] = convTitle,
                        ["conversationAvatarPath"] = convAvatar,
                        ["senderName"] = senderName,
                        ["senderUin"] = senderUin,
                        ["senderAvatarPath"] = FriendAvatarUrl(senderUin),
                        ["direction"] = isSelf ? "Outgoing" : "Incoming",
                        ["contentType"] = contentType,
                        ["text"] = text,
                        ["imagePath"] = imagePath,
                        ["audioPath"] = audioPath,
                        ["voiceSeconds"] = voiceSeconds,
                        ["elements"] = BuildElements(msg.Entities),
                        ["time"] = new DateTimeOffset(DateTime.SpecifyKind(msg.Time, DateTimeKind.Utc)).ToString("o"),
                        ["state"] = "Sent",
                    };
                    if (!string.IsNullOrEmpty(replyToSender)) wire["replyToSender"] = replyToSender;
                    if (!string.IsNullOrEmpty(replyToText)) wire["replyToText"] = replyToText;
                    // Group file ID for history messages (same logic as OnBotMessage)
                    if (contentType is "FileMsg" or "File")
                    {
                        var gf = msg.Entities.OfType<GroupFileEntity>().FirstOrDefault();
                        if (gf != null)
                        {
                            wire["fileId"] = gf.FileId;
                            wire["fileName"] = gf.FileName;
                            wire["fileSize"] = FormatFileSize(gf.FileSize);
                            Console.WriteLine($"[FileDL] history file: id={gf.FileId[..Math.Min(30, gf.FileId.Length)]} name={gf.FileName}");
                        }
                    }

                    // Insert by time so latest-page and older-page pulls land correctly.
                    var insertAt = list.Count;
                    var msgTime = (string)wire["time"]!;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (string.CompareOrdinal((string?)list[i]["time"], msgTime) > 0)
                        {
                            insertAt = i;
                            break;
                        }
                    }
                    list.Insert(insertAt, wire);
                    _rawMessages[msgId] = msg;
                    wireMessages.Add(wire);
                }

                // Keep conversation preview in sync when we backfilled into an empty chat.
                if (wireMessages.Count > 0)
                {
                    var last = list.Count > 0 ? list[list.Count - 1] : null;
                    if (last != null)
                    {
                        var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == conversationId);
                        if (conv != null)
                        {
                            var ct = (string?)last["contentType"] ?? "Text";
                            var tx = (string?)last["text"];
                            conv["preview"] = PreviewFor(ct, tx);
                            if (last["time"] is JsonValue tv && tv.TryGetValue<string>(out var lt))
                                conv["lastTime"] = lt;
                        }
                    }
                }
            }

            var arr = new JsonArray();
            foreach (var w in wireMessages.OrderBy(w => (string?)w["time"]).ThenBy(w => (string?)w["id"]))
                arr.Add(Clone(w));

            // Full cloud page => more history likely remains. Empty cloud page => stop.
            // Partial page => usually end of history (still allow one more client try if we
            // inserted something, in case the page boundary is soft).
            var hasMore = fetched.Count >= clampedCount;
            if (fetched.Count == 0) hasMore = false;

            var data = new JsonObject
            {
                ["messages"] = arr,
                ["hasMore"] = hasMore,
            };
            Console.WriteLine($"[GetEarlier] returning {wireMessages.Count} new / {fetched.Count} fetched, hasMore={hasMore}");
            return (data, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GetEarlierMessagesAsync({conversationId}) failed: {ex}");
            // Surface a clearer reason for the common "friend UID not in cache yet" failure.
            var msg = ex.Message;
            if (ex is InvalidTargetException || msg.Contains("InvalidTarget", StringComparison.OrdinalIgnoreCase))
                msg = "无法解析好友UID（好友列表可能尚未加载完成，请稍后重试）";
            return (null, msg);
        }
    }

    /// <summary>Ensure peer UIN has a cached NT uid so GetRoamMessage/GetC2CMessage don't
    /// throw InvalidTargetException. Friend list load is the normal path; stranger resolve is
    /// a fallback for edge cases (not on friend list but still have a conv id).</summary>
    private static async Task EnsureFriendUidAsync(BotContext bot, long peerUin)
    {
        // ResolveFriend triggers FetchFriends if needed and populates the uin→uid map.
        var friend = await bot.FetchFriends(refresh: false);
        if (friend.Any(f => f.Uin == peerUin)) return;

        // Refresh once more in case the list was stale/partial.
        friend = await bot.FetchFriends(refresh: true);
        if (friend.Any(f => f.Uin == peerUin)) return;

        // Last resort: FetchStranger may still populate cache entries used by ResolveCachedUid
        // in some Lagrange builds; even if not, we tried.
        try { await bot.FetchStranger(peerUin); }
        catch (Exception ex) { Console.WriteLine($"[GetEarlier] FetchStranger({peerUin}) failed: {ex.Message}"); }
    }

    /// <summary>Recalls a previously sent/received message. Only messages WE sent (Outgoing --
    /// i.e. the recorded BotMessage's Contact is us, per LagrangeV2's SendFriendMessage/
    /// SendGroupMessage convention of stamping Contact=self, Receiver=peer) may be recalled;
    /// Tencent would reject recalling someone else's message anyway, but checking locally avoids
    /// the round trip and gives a clearer reason string.</summary>
    public async Task<JsonObject> RecallMessageAsync(string conversationId, string messageId)
    {
        var bot = _bot;
        if (bot == null) return new JsonObject { ["recalled"] = false, ["reason"] = "not-online" };

        BotMessage? msg;
        lock (_gate) { _rawMessages.TryGetValue(messageId, out msg); }
        if (msg == null) return new JsonObject { ["recalled"] = false, ["reason"] = "unknown-message" };
        if (msg.Contact.Uin != bot.BotUin) return new JsonObject { ["recalled"] = false, ["reason"] = "not-own-message" };

        try
        {
            await bot.RecallMessage(msg);
            lock (_gate)
            {
                if (_messages.TryGetValue(conversationId, out var list))
                    list.RemoveAll(m => (string)m["id"]! == messageId);
                _rawMessages.Remove(messageId);
            }
            return new JsonObject { ["recalled"] = true, ["reason"] = null };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] RecallMessageAsync({conversationId}, {messageId}) failed: {ex.Message}");
            return new JsonObject { ["recalled"] = false, ["reason"] = ex.Message };
        }
    }

    /// <summary>Sends a "拍一拍" nudge. Friend (f) conversations nudge the peer themselves
    /// (targetUin is ignored -- SendFriendNudge always targets the peer, there's no one else
    /// to nudge in a 1:1 chat); group (g) conversations require targetUin (the uin of the
    /// member being nudged) since a group has many members.</summary>
    public async Task<(JsonObject? data, string? error)> SendNudgeAsync(string conversationId, long targetUin)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        var kind = conversationId.Length >= 2 ? conversationId[0] : '\0';
        if ((kind != 'f' && kind != 'g') || !long.TryParse(conversationId.AsSpan(1), out var peerUin) || peerUin <= 0)
            return (null, "invalid-conversation");
        if (kind == 'g' && targetUin <= 0)
            return (null, "invalid-target");

        try
        {
            if (kind == 'g') await bot.SendGroupNudge(peerUin, targetUin);
            else await bot.SendFriendNudge(peerUin, targetUin > 0 ? targetUin : null);
            return (new JsonObject { ["sent"] = true }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SendNudgeAsync({conversationId}, {targetUin}) failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Uploads a new bot avatar from a base64-encoded image (already resized/compressed
    /// client-side to well under the socket's 1MB single-frame cap).</summary>
    public async Task<(JsonObject? data, string? error)> SetAvatarAsync(string imageBase64)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (string.IsNullOrEmpty(imageBase64)) return (null, "invalid-image");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(imageBase64); }
        catch (FormatException) { return (null, "invalid-image"); }

        try
        {
            using var stream = new MemoryStream(bytes);
            var ok = await bot.SetBotAvatar(stream);
            return (new JsonObject { ["ok"] = ok }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SetAvatarAsync failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Resolves a direct CDN URL for image/video/voice media, looked up from the
    /// already-recorded raw BotMessage by wire id. Prefer a FileUrl already filled by
    /// Postprocess; otherwise call GetNTV2RichMediaUrl (async, so this stays on-demand when
    /// the client taps rather than running on every receive).</summary>
    public async Task<(JsonObject? data, string? error)> GetMediaUrlAsync(string messageId)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        BotMessage? msg;
        lock (_gate) { _rawMessages.TryGetValue(messageId, out msg); }
        if (msg == null) return (null, "no-media");

        var media = msg.Entities.OfType<RichMediaEntityBase>()
            .FirstOrDefault(e => e is ImageEntity or VideoEntity or RecordEntity);
        if (media == null) return (null, "no-media");

        // Images usually already have FileUrl after MessagePacker.Postprocess; reuse it so
        // a full-screen open does not pay another download-ticket round-trip.
        if (!string.IsNullOrEmpty(media.FileUrl))
            return (new JsonObject { ["url"] = media.FileUrl }, null);

        if (string.IsNullOrEmpty(media.FileUuid)) return (null, "no-media");

        try
        {
            var url = await bot.GetNTV2RichMediaUrl(media.FileUuid);
            if (string.IsNullOrEmpty(url)) return (null, "no-media");
            // Patch the wire snapshot so re-opened chats carry imagePath for the bubble.
            // (FileUrl on the entity has an internal setter — we can't write it here.)
            lock (_gate)
            {
                foreach (var kv in _messages)
                {
                    var row = kv.Value.FirstOrDefault(m => (string?)m["id"] == messageId);
                    if (row == null) continue;
                    row["imagePath"] = url;
                    break;
                }
            }
            return (new JsonObject { ["url"] = url }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GetMediaUrlAsync({messageId}) failed: {ex.Message}");
            return (null, "no-media");
        }
    }

    /// <summary>Leaves a group. Friend (f) conversations have no equivalent operation and report
    /// it honestly instead of pretending to succeed.</summary>
    public async Task<JsonObject> QuitGroupAsync(string conversationId)
    {
        var bot = _bot;
        if (bot == null) return new JsonObject { ["left"] = false, ["reason"] = "not-online" };
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return new JsonObject { ["left"] = false, ["reason"] = "not-a-group" };

        try
        {
            await bot.GroupQuit(groupUin);
            lock (_gate)
            {
                _conversations.RemoveAll(c => (string)c["id"]! == conversationId);
                _messages.Remove(conversationId);
                _groupMembers.Remove(conversationId);
            }
            return new JsonObject { ["left"] = true, ["reason"] = null };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] QuitGroupAsync({conversationId}) failed: {ex.Message}");
            return new JsonObject { ["left"] = false, ["reason"] = ex.Message };
        }
    }

    // ---- voice playback (P0-1) ----

    /// <summary>Downloads voice message audio from CDN and returns base64 bytes for client playback.
    /// The CDN typically serves SILK-encoded audio which UWP MediaPlayer cannot play natively.
    /// This method downloads the raw bytes and returns them with format detection so the client
    /// can attempt playback or show an appropriate message.</summary>
    public async Task<(JsonObject? data, string? error)> GetVoicePlayableAsync(string messageId)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        BotMessage? msg;
        lock (_gate) { _rawMessages.TryGetValue(messageId, out msg); }
        if (msg == null) return (null, "unknown-message");

        var record = msg.Entities.OfType<RecordEntity>().FirstOrDefault();
        if (record == null) return (null, "not-a-voice-message");

        try
        {
            // Resolve the CDN URL
            var url = record.FileUrl;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(record.FileUuid))
            {
                url = await bot.GetNTV2RichMediaUrl(record.FileUuid);
            }
            if (string.IsNullOrEmpty(url)) return (null, "no-voice-url");

            // Download the audio bytes from CDN
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return (null, "empty-voice-data");

            // Detect format by magic bytes
            string format = "unknown";
            if (bytes.Length > 10)
            {
                // TenSilk: starts with 0x02 followed by #!SILK_V3
                if (bytes[0] == 0x02 && bytes.Length > 11 &&
                    bytes[1] == (byte)'#' && bytes[2] == (byte)'!' &&
                    bytes[3] == (byte)'S' && bytes[4] == (byte)'I' &&
                    bytes[5] == (byte)'L' && bytes[6] == (byte)'K')
                    format = "silk";
                // Standard SILK_V3 without Tencent header
                else if (bytes[0] == (byte)'#' && bytes[1] == (byte)'!' &&
                         bytes[2] == (byte)'S' && bytes[3] == (byte)'I' &&
                         bytes[4] == (byte)'L' && bytes[5] == (byte)'K')
                    format = "silk";
                // AMR
                else if (bytes[0] == (byte)'#' && bytes[1] == (byte)'!' &&
                         bytes[2] == (byte)'A' && bytes[3] == (byte)'M' &&
                         bytes[4] == (byte)'R')
                    format = "amr";
                // MP3 (ID3 or sync word)
                else if ((bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3') ||
                         (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0))
                    format = "mp3";
                // OGG
                else if (bytes[0] == (byte)'O' && bytes[1] == (byte)'g' &&
                         bytes[2] == (byte)'g' && bytes[3] == (byte)'S')
                    format = "ogg";
                // WAV
                else if (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
                         bytes[2] == (byte)'F' && bytes[3] == (byte)'F')
                    format = "wav";
            }

            var duration = (int)record.RecordLength;
            Console.WriteLine($"[GetVoicePlayable] {messageId}: {bytes.Length} bytes, format={format}, duration={duration}s");

            // Prefer WAV for the UWP client. AMR/MP3/WAV/OGG may play; SILK must be decoded.
            var wav = await TryTranscodeVoiceToWavAsync(bytes, format);
            if (wav != null && wav.Length > 0)
            {
                Console.WriteLine($"[GetVoicePlayable] {messageId}: transcoded to WAV {wav.Length}B");
                return (new JsonObject
                {
                    ["audioBase64"] = Convert.ToBase64String(wav),
                    ["format"] = "wav",
                    ["duration"] = duration,
                }, null);
            }

            if (format is "mp3" or "wav" or "ogg" or "amr")
            {
                return (new JsonObject
                {
                    ["audioBase64"] = Convert.ToBase64String(bytes),
                    ["format"] = format,
                    ["duration"] = duration,
                }, null);
            }

            // Do not return a silent mock WAV (previous bug): client would "play" nothing.
            return (null, format == "silk"
                ? "silk-decode-unavailable（需服务器安装 silk_v3_decoder 或 ffmpeg 可解码的 SILK 工具链）"
                : "unsupported-voice-format:" + format);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GetVoicePlayableAsync({messageId}) failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Convert downloaded PTT bytes to WAV via ffmpeg (+ optional silk_v3_decoder).
    /// Returns null when tools are missing or conversion fails.</summary>
    private static async Task<byte[]?> TryTranscodeVoiceToWavAsync(byte[] bytes, string format)
    {
        if (bytes == null || bytes.Length == 0) return null;
        if (format == "wav") return bytes;

        try
        {
            var work = Path.Combine(Path.GetTempPath(), "qqr_voice_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string inputPath;
                if (format == "silk")
                {
                    // TenSilk: leading 0x02 before #!SILK_V3 — strip for standard silk tools.
                    var silk = bytes;
                    if (bytes.Length > 10 && bytes[0] == 0x02 && bytes[1] == (byte)'#')
                        silk = bytes.AsSpan(1).ToArray();

                    var silkPath = Path.Combine(work, "in.silk");
                    var pcmPath = Path.Combine(work, "out.pcm");
                    await File.WriteAllBytesAsync(silkPath, silk);

                    // Prefer silk_v3_decoder / decoder in PATH (kn007 silk-v3-decoder style).
                    var decoded = await TryRunProcessAsync("silk_v3_decoder", $"\"{silkPath}\" \"{pcmPath}\"")
                               || await TryRunProcessAsync("decoder", $"\"{silkPath}\" \"{pcmPath}\"")
                               || await TryRunProcessAsync("silk_decoder", $"\"{silkPath}\" \"{pcmPath}\"");
                    if (!decoded || !File.Exists(pcmPath) || new FileInfo(pcmPath).Length == 0)
                    {
                        // Some builds accept silk via ffmpeg (rare). Last attempt.
                        inputPath = silkPath;
                    }
                    else
                    {
                        // QQ SILK is typically 24kHz mono s16le PCM after decode.
                        var wavPath = Path.Combine(work, "out.wav");
                        var ok = await TryRunProcessAsync("ffmpeg",
                            $"-y -f s16le -ar 24000 -ac 1 -i \"{pcmPath}\" \"{wavPath}\"");
                        if (ok && File.Exists(wavPath))
                            return await File.ReadAllBytesAsync(wavPath);
                        return null;
                    }
                }
                else
                {
                    var ext = format is "mp3" or "amr" or "ogg" ? format : "bin";
                    inputPath = Path.Combine(work, "in." + ext);
                    await File.WriteAllBytesAsync(inputPath, bytes);
                }

                {
                    var wavPath = Path.Combine(work, "out.wav");
                    var ok = await TryRunProcessAsync("ffmpeg", $"-y -i \"{inputPath}\" \"{wavPath}\"");
                    if (ok && File.Exists(wavPath))
                        return await File.ReadAllBytesAsync(wavPath);
                }
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] TryTranscodeVoiceToWavAsync({format}) failed: {ex.Message}");
        }
        return null;
    }

    private static async Task<bool> TryRunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            // Don't deadlock on full pipes.
            var stderr = proc.StandardError.ReadToEndAsync();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var finished = proc.WaitForExit(15000);
            if (!finished)
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            await Task.WhenAll(stderr, stdout);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- file transfer (P0-4) ----

    /// <summary>Returns a temporary download URL for a group file by its fileId.
    /// Uses LagrangeV2's GroupFSDownload API.</summary>
    public async Task<(JsonObject? data, string? error)> GetFileDownloadUrlAsync(string conversationId, string fileId)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return (null, "not-a-group");
        if (string.IsNullOrEmpty(fileId)) return (null, "empty-file-id");
        Console.WriteLine($"[FileDL] group={groupUin} fileId={fileId[..Math.Min(40, fileId.Length)]} len={fileId.Length}");

        try
        {
            var url = await bot.GroupFSDownload(groupUin, fileId);
            if (!string.IsNullOrEmpty(url)) return (new JsonObject { ["url"] = url }, null);

            // Fallback: try to get it from NTV2 RichMedia
            try { url = await bot.GetNTV2RichMediaUrl(fileId); }
            catch { /* ignore */ }
            if (!string.IsNullOrEmpty(url)) return (new JsonObject { ["url"] = url }, null);

            return (null, "no-download-url");
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            // Friendly messages for common errors
            if (msg.Contains("-103") || msg.Contains("file not exist"))
                msg = "文件已过期或已被删除";
            Console.WriteLine($"[!] GetFileDownloadUrlAsync({conversationId}, {fileId}) failed: {ex.Message}");
            return (null, msg);
        }
    }

    // ---- group notifications (P1-2) ----

    /// <summary>Fetches pending group join/invite notifications.</summary>
    public async Task<(JsonObject? data, string? error)> GetGroupNotificationsAsync()
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        try
        {
            var notifications = await bot.FetchGroupNotifications(20);
            var arr = new JsonArray();
            foreach (var n in notifications)
            {
                var item = new JsonObject
                {
                    ["groupUin"] = n.GroupUin,
                    ["sequence"] = (double)n.Sequence,
                    ["targetUin"] = n.TargetUin,
                };
                // Set type and details based on notification type
                if (n is BotGroupJoinNotification join)
                {
                    item["type"] = "join";
                    item["initiatorUin"] = join.OperatorUin ?? join.TargetUin;
                    item["initiatorNickname"] = (join.OperatorUin ?? join.TargetUin).ToString();
                    item["message"] = join.Comment;
                    item["isFiltered"] = join.IsFiltered;
                    item["avatarPath"] = FriendAvatarUrl(join.TargetUin);
                }
                else if (n is BotGroupInviteNotification invite)
                {
                    item["type"] = "invite";
                    item["initiatorUin"] = invite.InviterUin;
                    item["initiatorNickname"] = invite.InviterUin.ToString();
                    item["isFiltered"] = invite.IsFiltered;
                    item["avatarPath"] = FriendAvatarUrl(invite.InviterUin);
                }
                else
                {
                    item["type"] = "other";
                    item["isFiltered"] = false;
                }
                item["groupAvatarPath"] = GroupAvatarUrl(n.GroupUin);
                arr.Add(item);
            }
            return (new JsonObject { ["notifications"] = arr }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GetGroupNotificationsAsync failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Handles (accept/reject) a group notification.</summary>
    public async Task<(JsonObject? data, string? error)> HandleGroupNotificationAsync(
        long groupUin, ulong sequence, string type, string operate, string? message, bool isFiltered = false)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");

        try
        {
            var notifType = type switch
            {
                "join" => BotGroupNotificationType.Join,
                "invite" => BotGroupNotificationType.Invite,
                _ => BotGroupNotificationType.Join,
            };
            var op = operate switch
            {
                "accept" or "allow" => GroupNotificationOperate.Allow,
                "reject" or "deny" => GroupNotificationOperate.Deny,
                "ignore" => GroupNotificationOperate.Ignore,
                _ => GroupNotificationOperate.Allow,
            };
            await bot.SetGroupNotification(groupUin, sequence, notifType, isFiltered, op, message ?? "");
            return (new JsonObject { ["ok"] = true }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] HandleGroupNotificationAsync failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>Group message emoji reaction via Lagrange <c>SetGroupReaction</c>.</summary>
    public async Task<(JsonObject? data, string? error)> SetGroupReactionAsync(
        string conversationId, string messageId, string code, bool isAdd)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (conversationId.Length < 2 || conversationId[0] != 'g'
            || !long.TryParse(conversationId.AsSpan(1), out var groupUin) || groupUin <= 0)
            return (null, "not-a-group");
        if (string.IsNullOrEmpty(code)) return (null, "empty-code");

        // Wire id for groups: "g{uin}:{sequence}"
        ulong sequence = 0;
        var colon = messageId.LastIndexOf(':');
        if (colon < 0 || !ulong.TryParse(messageId.AsSpan(colon + 1), out sequence) || sequence == 0)
            return (null, "bad-message-id");

        try
        {
            await bot.SetGroupReaction(groupUin, sequence, code, isAdd);
            return (new JsonObject { ["ok"] = true, ["code"] = code, ["isAdd"] = isAdd }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SetGroupReactionAsync failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    // ---- QQ 空间 / 动态 via webhook ----

    /// <summary>Return cached space feed (newest first) with pagination state.</summary>
    public JsonObject GetSpaceFeed()
    {
        lock (_gate)
        {
            // Always expose newest-first. Incoming extractors may append in arbitrary order.
            var ordered = _spaceFeed
                .OrderByDescending(item =>
                {
                    var raw = (string?)item["time"] ?? (string?)item["timeText"] ?? "";
                    return DateTimeOffset.TryParse(raw, out var dto) ? dto : DateTimeOffset.MinValue;
                })
                .ThenByDescending(item => (string?)item["id"] ?? "")
                .ToList();
            var arr = new JsonArray();
            foreach (var item in ordered) arr.Add(Clone(item));
            return new JsonObject { ["moments"] = arr, ["hasMore"] = _spaceFeedHasMore };
        }
    }

    /// <summary>Update the logged-in user's like state for a webhook-ingested space post.</summary>
    public JsonObject SetSpaceLike(string momentId, bool isLiked)
    {
        if (string.IsNullOrWhiteSpace(momentId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-moment-id" };

        lock (_gate)
        {
            var item = _spaceFeed.FirstOrDefault(x => (string?)x["id"] == momentId);
            if (item == null)
                return new JsonObject { ["ok"] = false, ["reason"] = "moment-not-found" };

            var old = item["isLiked"]?.GetValue<bool>() ?? false;
            var count = (int)(item["likeCount"]?.GetValue<double>() ?? 0);
            if (old != isLiked)
                count = Math.Max(0, count + (isLiked ? 1 : -1));

            item["isLiked"] = isLiked;
            item["likeCount"] = count;
        }

        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "spaceFeedUpdated",
            ["data"] = new JsonObject { ["momentId"] = momentId, ["likeChanged"] = true },
        }.ToJsonString());

        return new JsonObject { ["ok"] = true, ["id"] = momentId, ["isLiked"] = isLiked };
    }

    /// <summary>
    /// Ingest one or more space posts from an external web 空间 / 爬虫 webhook.
    /// Accepts a single object or { "items": [ ... ] }.
    /// </summary>
    public JsonObject IngestSpaceWebhook(JsonNode? body)
    {
        if (body == null) return new JsonObject { ["ok"] = false, ["reason"] = "empty-body" };

        var added = 0;
        void AddOne(JsonObject raw)
        {
            var id = raw["id"]?.GetValue<string>()
                     ?? ("wh_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N")[..6]);
            var author = raw["author"]?.GetValue<string>()
                         ?? raw["authorName"]?.GetValue<string>()
                         ?? "好友";
            var avatar = raw["avatar"]?.GetValue<string>()
                         ?? raw["authorAvatar"]?.GetValue<string>()
                         ?? FriendAvatarUrl((long)(raw["authorUin"]?.GetValue<double>() ?? 0));
            var text = raw["text"]?.GetValue<string>() ?? raw["content"]?.GetValue<string>() ?? "";
            var time = raw["time"]?.GetValue<string>()
                       ?? raw["timeText"]?.GetValue<string>()
                       ?? DateTimeOffset.UtcNow.ToString("o");

            var images = new JsonArray();
            if (raw["images"] is JsonArray ja)
            {
                foreach (var n in ja)
                {
                    var u = n?.GetValue<string>();
                    if (!string.IsNullOrEmpty(u)) images.Add(u);
                }
            }
            else if (raw["image"] is JsonValue one)
            {
                var u = one.GetValue<string>();
                if (!string.IsNullOrEmpty(u)) images.Add(u);
            }

            var comments = new JsonArray();
            if (raw["comments"] is JsonArray rawComments)
            {
                foreach (var node in rawComments)
                {
                    if (node is not JsonObject comment) continue;
                    var commentAuthor = comment["author"]?.GetValue<string>()
                                        ?? comment["authorName"]?.GetValue<string>()
                                        ?? "好友";
                    var commentText = comment["text"]?.GetValue<string>()
                                      ?? comment["content"]?.GetValue<string>()
                                      ?? "";
                    if (!string.IsNullOrWhiteSpace(commentText))
                    {
                        comments.Add(new JsonObject
                        {
                            ["author"] = commentAuthor,
                            ["text"] = commentText,
                        });
                    }
                }
            }

            var item = new JsonObject
            {
                ["id"] = id,
                ["authorName"] = author,
                ["authorAvatarPath"] = avatar,
                ["text"] = text,
                ["time"] = time,
                ["timeText"] = FormatSpaceTime(time),
                ["images"] = images,
                ["comments"] = comments,
                ["videoPath"] = raw["video"]?.GetValue<string>() ?? raw["videoPath"]?.GetValue<string>(),
                ["likeCount"] = (int)(raw["likeCount"]?.GetValue<double>() ?? 0),
                ["isLiked"] = raw["isLiked"]?.GetValue<bool>() ?? false,
            };

            lock (_gate)
            {
                // De-dupe by id
                _spaceFeed.RemoveAll(x => (string?)x["id"] == id);
                _spaceFeed.Insert(0, item);
                while (_spaceFeed.Count > MaxSpaceFeedItems)
                    _spaceFeed.RemoveAt(_spaceFeed.Count - 1);
            }
            added++;
        }

        if (body is JsonObject obj)
        {
            if (obj["items"] is JsonArray items)
            {
                foreach (var n in items)
                    if (n is JsonObject o) AddOne(o);
            }
            else
            {
                AddOne(obj);
            }
        }
        else if (body is JsonArray arr)
        {
            foreach (var n in arr)
                if (n is JsonObject o) AddOne(o);
        }
        else
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid-json" };
        }

        if (added > 0)
        {
            Broadcast?.Invoke(new JsonObject
            {
                ["type"] = "spaceFeedUpdated",
                ["data"] = new JsonObject { ["added"] = added },
            }.ToJsonString());
        }

        return new JsonObject { ["ok"] = true, ["added"] = added };
    }

    private static string FormatSpaceTime(string isoOrText)
    {
        if (DateTimeOffset.TryParse(isoOrText, out var t))
        {
            var now = DateTimeOffset.Now;
            var diff = now - t.ToLocalTime();
            if (diff.TotalMinutes < 1) return "刚刚";
            if (diff.TotalHours < 1) return (int)diff.TotalMinutes + " 分钟前";
            if (diff.TotalDays < 1) return (int)diff.TotalHours + " 小时前";
            if (diff.TotalDays < 7) return (int)diff.TotalDays + " 天前";
            return t.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return isoOrText;
    }

    public async Task<(JsonObject? data, string? error)> SendAsync(
        string conversationId, string text, string? replyToId = null,
        string contentType = "Text", string? placeName = null, string? address = null, string? thumb = null,
        string? imageBase64 = null, JsonNode? imagesBase64Node = null, string? audioBase64 = null, int voiceSeconds = 0,
        string? fileBase64 = null, string? fileName = null, string? mentionsJson = null)
    {
        Console.WriteLine($"[SendAsync] request received: conversationId={conversationId}, contentType={contentType}, textLen={text?.Length ?? 0}, imageB64={imageBase64?.Length ?? 0}, audioB64={audioBase64?.Length ?? 0}, replyToId={replyToId ?? "(none)"}");

        var bot = _bot;
        if (bot == null) { Console.WriteLine("[SendAsync] rejected: not-online"); return (null, "not-online"); }
        // Strict id check: only "f{uin}" / "g{groupUin}" with a positive uin may reach a real
        // Tencent API call. The old lenient parse routed anything with a numeric tail ("g-100",
        // "G123", stray mock ids) into SendFriendMessage/SendGroupMessage.
        var kind = conversationId.Length >= 2 ? conversationId[0] : '\0';
        if ((kind != 'f' && kind != 'g') || !long.TryParse(conversationId.AsSpan(1), out var uin) || uin <= 0)
        {
            Console.WriteLine($"[SendAsync] rejected: invalid-conversation ({conversationId})");
            return (null, "invalid-conversation");
        }

        // Collect image payloads: single imageBase64 and/or imagesBase64 array (图文混排 / 多图).
        var imageList = new List<byte[]>();
        void AddImageB64(string? b64)
        {
            if (string.IsNullOrEmpty(b64)) return;
            try
            {
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length == 0) return;
                if (bytes.Length > 1500 * 1024)
                    throw new InvalidOperationException("image-too-large");
                // Dedup identical first frame when client also puts it in imagesBase64[0].
                if (imageList.Count > 0 && imageList[0].Length == bytes.Length && imageList[0].AsSpan().SequenceEqual(bytes))
                    return;
                imageList.Add(bytes);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("invalid-image");
            }
        }

        try
        {
            AddImageB64(imageBase64);
            if (imagesBase64Node is JsonArray imgsArr)
            {
                foreach (var n in imgsArr)
                {
                    if (n is JsonValue jv && jv.TryGetValue<string>(out var s))
                        AddImageB64(s);
                }
            }
            else if (imagesBase64Node is JsonValue single && single.TryGetValue<string>(out var arrText)
                     && !string.IsNullOrWhiteSpace(arrText) && arrText.TrimStart().StartsWith('['))
            {
                try
                {
                    if (JsonNode.Parse(arrText) is JsonArray parsed)
                    {
                        foreach (var n in parsed)
                        {
                            if (n is JsonValue jv && jv.TryGetValue<string>(out var s))
                                AddImageB64(s);
                        }
                    }
                }
                catch { /* ignore bad array */ }
            }
        }
        catch (InvalidOperationException ioe)
        {
            return (null, ioe.Message);
        }

        // Text / location-as-text / image (base64 JPEG) / voice (base64 audio; prefer silk/amr) / file.
        // Mixed = caption text + image(s) in one MessageBuilder chain.
        string? sendText = null;
        byte[]? audioBytes = null;
        byte[]? fileBytes = null;
        switch (contentType)
        {
            case "Text":
            case "":
            case "Mixed":
                if (string.IsNullOrWhiteSpace(text) && imageList.Count == 0)
                    return (null, "empty-text");
                if (!string.IsNullOrWhiteSpace(text)) sendText = text!;
                break;
            case "Location":
                sendText = string.IsNullOrEmpty(address) ? $"[位置] {placeName}" : $"[位置] {placeName}（{address}）";
                break;
            case "Image":
            case "Sticker":
                if (imageList.Count == 0) return (null, "empty-image");
                // Optional caption on an "Image" send is allowed (treated as 图文混排).
                if (!string.IsNullOrWhiteSpace(text)) sendText = text!;
                break;
            case "Voice":
                if (string.IsNullOrEmpty(audioBase64)) return (null, "empty-audio");
                try { audioBytes = Convert.FromBase64String(audioBase64); }
                catch (FormatException) { return (null, "invalid-audio"); }
                if (audioBytes.Length == 0) return (null, "empty-audio");
                if (audioBytes.Length > 1500 * 1024) return (null, "audio-too-large");
                break;
            case "File":
                if (string.IsNullOrEmpty(fileBase64)) return (null, "empty-file");
                try { fileBytes = Convert.FromBase64String(fileBase64); }
                catch (FormatException) { return (null, "invalid-file"); }
                if (fileBytes.Length == 0) return (null, "empty-file");
                if (string.IsNullOrEmpty(fileName)) fileName = "file";
                break;
            default:
                return (null, $"unsupported-content：{contentType} 暂不支持发送到真实QQ（目前支持文本/引用回复/位置/图片/图文混排/语音/文件）");
        }

        try
        {
            var builder = new MessageBuilder();
            string? replyToSender = null;
            string? replyToText = null;
            if (!string.IsNullOrEmpty(replyToId))
            {
                BotMessage? source;
                lock (_gate) { _rawMessages.TryGetValue(replyToId, out source); }
                if (source != null)
                {
                    builder.Reply(source);
                    // Prefer the already-recorded wire snapshot for the quote header: its
                    // senderName went through the self-echo normalization, whereas the raw
                    // BotMessage's Contact for our own echoed DMs is LagrangeV2's placeholder
                    // whose Nickname is the internal Uid string.
                    var (metaSender, metaText) = FindMessageMeta(conversationId, replyToId);
                    replyToSender = metaSender
                        ?? (source.Contact != null && source.Contact.Uin == bot.BotUin
                            ? (bot.BotInfo?.Name ?? bot.BotUin.ToString())
                            : source.Contact?.Nickname);
                    replyToText = metaText;
                }
            }

            // Caption / text first, then image(s) — QQ clients render this as 图文混排.
            if (!string.IsNullOrEmpty(sendText) && audioBytes == null && fileBytes == null)
            {
                if (!string.IsNullOrEmpty(mentionsJson) && kind == 'g')
                {
                    try
                    {
                        var mentions = JsonNode.Parse(mentionsJson) as JsonArray;
                        if (mentions != null && mentions.Count > 0)
                        {
                            var sortedMentions = new List<(long Uin, string Display, int Offset, int Length)>();
                            var workText = sendText!;
                            foreach (var m in mentions)
                            {
                                if (m == null) continue;
                                var mentionUin = (long)(double)m["uin"]!;
                                var display = (string)m["display"]!;
                                var search = "@" + display + " ";
                                var idx = workText.IndexOf(search);
                                if (idx >= 0)
                                {
                                    sortedMentions.Add((mentionUin, display, idx, search.Length));
                                    // replace with placeholders so we don't match the same one twice if names are identical
                                    workText = workText.Substring(0, idx) + new string('\0', search.Length) + workText.Substring(idx + search.Length);
                                }
                            }

                            sortedMentions = sortedMentions.OrderBy(m => m.Offset).ToList();

                            var lastEnd = 0;
                            foreach (var mention in sortedMentions)
                            {
                                if (mention.Offset > lastEnd)
                                {
                                    var slice = workText.Substring(lastEnd, mention.Offset - lastEnd).Replace("\0", "");
                                    if (slice.Length > 0) builder.Text(slice);
                                }
                                builder.Mention(mention.Uin, mention.Display);
                                lastEnd = mention.Offset + mention.Length;
                            }
                            if (lastEnd < workText.Length)
                            {
                                var slice = workText.Substring(lastEnd).Replace("\0", "");
                                if (slice.Length > 0) builder.Text(slice);
                            }
                        }
                        else
                        {
                            builder.Text(sendText!);
                        }
                    }
                    catch
                    {
                        builder.Text(sendText!);
                    }
                }
                else
                {
                    builder.Text(sendText!);
                }
            }

            if (imageList.Count > 0 && audioBytes == null && fileBytes == null)
            {
                // MessageBuilder.Image takes ownership when disposeOnCompletion:true (MemoryStream
                // closed after Highway upload). Sticker uses subType=1 so Tencent renders it as
                // an emoticon-sized image rather than a full photo bubble.
                var subType = contentType == "Sticker" ? 1 : 0;
                var summary = contentType == "Sticker" ? "[表情]" : "[图片]";
                foreach (var imageBytes in imageList)
                    builder.Image(new MemoryStream(imageBytes), summary, subType, disposeOnCompletion: true);
            }
            else if (audioBytes != null)
            {
                // QQ expects silk/amr for PTT. Client sends m4a. We try to transcode to AMR using ffmpeg.
                byte[] convertedBytes = audioBytes;
                try
                {
                    var tempIn = Path.GetTempFileName() + ".m4a";
                    var tempOut = Path.GetTempFileName() + ".amr";
                    File.WriteAllBytes(tempIn, audioBytes);
                    
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-y -i \"{tempIn}\" -ar 8000 -ab 12.2k \"{tempOut}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(5000);
                        if (proc.ExitCode == 0 && File.Exists(tempOut))
                        {
                            convertedBytes = File.ReadAllBytes(tempOut);
                        }
                    }
                    try { File.Delete(tempIn); } catch { }
                    try { File.Delete(tempOut); } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Voice transcode to AMR failed: {ex.Message}. Falling back to original M4A.");
                }
                builder.Record(new MemoryStream(convertedBytes), disposeOnCompletion: true);
            }
            else if (fileBytes != null)
            {
                // File send uses a separate Lagrange API (not MessageBuilder), handled below.
            }
            else if (string.IsNullOrEmpty(sendText))
            {
                return (null, "empty-message");
            }
            // File sends use a separate API path, not MessageBuilder chain.
            if (fileBytes != null)
            {
                try
                {
                    using var fileStream = new MemoryStream(fileBytes);
                    string wireContentTypeF = "FileMsg";
                    string? wireTextF;
                    string? outgoingFileId = null;
                    if (kind == 'g')
                    {
                        var fileId = await bot.SendGroupFile(uin, fileStream, fileName);
                        outgoingFileId = fileId;
                        wireTextF = $"[文件] {fileName}";
                        Console.WriteLine($"[SendAsync] group file sent, fileId={fileId}");
                    }
                    else
                    {
                        var (seq, time) = await bot.SendFriendFile(uin, fileStream, fileName);
                        // Private offline-file download URL is not exposed by Lagrange Core.
                        // Keep a synthetic id so the client can still treat this as a file card;
                        // re-open uses the local cache the app saved on send.
                        var ts = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                        outgoingFileId = $"friend-file:{seq}:{ts}";
                        wireTextF = $"[文件] {fileName}";
                        Console.WriteLine($"[SendAsync] friend file sent, seq={seq}");
                    }
                    // File sends don't return a BotMessage, create a synthetic wire response
                    var fileMsgId = $"{conversationId}:file:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    var fileWire = new JsonObject
                    {
                        ["id"] = fileMsgId,
                        ["conversationId"] = conversationId,
                        ["senderName"] = bot.BotInfo?.Name ?? bot.BotUin.ToString(),
                        ["senderUin"] = bot.BotUin,
                        ["senderAvatarPath"] = FriendAvatarUrl(bot.BotUin),
                        ["direction"] = "Outgoing",
                        ["contentType"] = wireContentTypeF,
                        ["text"] = wireTextF,
                        ["fileName"] = fileName,
                        ["fileSize"] = FormatFileSize(fileBytes.Length),
                        ["fileId"] = outgoingFileId,
                        ["time"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["state"] = "Sent",
                    };
                    lock (_gate)
                    {
                        if (!_messages.TryGetValue(conversationId, out var list)) { list = new(); _messages[conversationId] = list; }
                        list.Add(fileWire);
                    }
                    BumpPreview(conversationId, wireTextF);
                    return (fileWire, null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] SendAsync file failed: {ex}");
                    return (null, "文件发送失败：" + ex.Message);
                }
            }

            var chain = builder.Build();

            // Group send needs our own BotGroupMember in cache (ResolveMember). History pulls
            // and sign 401s can leave the member list empty -- refresh before send so we don't
            // throw InvalidTargetException for a group we actually belong to.
            if (kind == 'g')
            {
                try { await bot.FetchMembers(uin, refresh: false); }
                catch (Exception ex) { Console.WriteLine($"[SendAsync] FetchMembers({uin}) warn: {ex.Message}"); }
            }

            BotMessage sent;
            try
            {
                sent = kind == 'g'
                    ? await bot.SendGroupMessage(uin, chain)
                    : await bot.SendFriendMessage(uin, chain);
            }
            catch (InvalidTargetException ex)
            {
                // Common for groups when self isn't in the cached member list yet.
                if (kind == 'g')
                {
                    Console.WriteLine($"[SendAsync] InvalidTarget on group send, refreshing members and retrying once: {ex.Message}");
                    try { await bot.FetchMembers(uin, refresh: true); }
                    catch { /* fall through */ }
                    try
                    {
                        sent = await bot.SendGroupMessage(uin, chain);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[!] SendAsync group retry failed: {ex2}");
                        return (null, "群发送失败：无法解析本账号在群内的成员信息（" + ex2.Message + "）");
                    }
                }
                else
                {
                    return (null, "发送失败：无效的好友目标（" + ex.Message + "）");
                }
            }

            // Prefer CDN URL from the uploaded ImageEntity when available; Postprocess isn't
            // always run on the send path, so FileUrl may still be null -- client falls back
            // to its local copy in that case.
            string? imagePath = null;
            string? audioPath = null;
            var wireVoiceSeconds = 0;
            string wireContentType;
            string? wireText;
            var sentImages = sent.Entities.OfType<ImageEntity>().ToList();
            var hasCaption = !string.IsNullOrWhiteSpace(sendText) && contentType != "Location";
            if (contentType is "Image" or "Sticker" or "Mixed" || (imageList.Count > 0 && contentType is "Text" or ""))
            {
                imagePath = sentImages.FirstOrDefault()?.FileUrl;
                if (hasCaption && sentImages.Count > 0)
                {
                    // 图文混排: keep caption as text; elements carry image URLs.
                    wireContentType = "Mixed";
                    wireText = sendText;
                }
                else if (contentType == "Sticker")
                {
                    wireContentType = "Sticker";
                    wireText = "[表情]";
                }
                else if (sentImages.Count > 1)
                {
                    wireContentType = "Mixed";
                    wireText = $"[图片×{sentImages.Count}]";
                }
                else
                {
                    wireContentType = "Image";
                    wireText = "[图片]";
                }
            }
            else if (contentType == "Voice")
            {
                wireContentType = "Voice";
                var rec = sent.Entities.OfType<RecordEntity>().FirstOrDefault();
                wireVoiceSeconds = voiceSeconds > 0 ? voiceSeconds : (int)(rec?.RecordLength ?? 0);
                wireText = $"[语音] {wireVoiceSeconds}\"";
                audioPath = rec?.FileUrl;
            }
            else if (contentType == "Location")
            {
                wireContentType = "Location";
                wireText = sendText;
            }
            else
            {
                wireContentType = "Text";
                wireText = text;
            }

            // Sign failure (HTTP 401 etc.) still lets the packet go out unsigned; Tencent may
            // return Result=0 with Sequence=0, which is not a real delivery. Treat as failure
            // so the UI doesn't show a fake "sent" bubble that never arrives on phone/watch.
            if (sent.Sequence == 0)
            {
                var reason = TokenSignProvider.LastFailureReason;
                Console.WriteLine($"[SendAsync] rejected: sequence=0 (likely sign failure). lastSign={reason ?? "(none)"}");
                return (null, string.IsNullOrEmpty(reason)
                    ? "发送失败：消息未真正投递（seq=0，检查签名服务 API Key 是否有效）"
                    : "发送失败：签名异常（" + reason + "）");
            }

            // For friend (C2C) sends, sent.Sequence is actually the random CLIENT sequence
            // (LagrangeV2's SendMessageService coalesces it into the same field); for group
            // sends it's the real server sequence. WireMessageId tags C2C ids by which space
            // they came from so they can never collide with OnBotMessage's server-sequence ids.
            var msgId = WireMessageId(conversationId, sent.Sequence, isServerSequence: kind != 'f');
            var wire = new JsonObject
            {
                ["id"] = msgId,
                ["conversationId"] = conversationId,
                ["senderName"] = bot.BotInfo?.Name ?? bot.BotUin.ToString(),
                ["senderUin"] = bot.BotUin,
                ["senderAvatarPath"] = FriendAvatarUrl(bot.BotUin),
                ["direction"] = "Outgoing",
                ["contentType"] = wireContentType,
                ["text"] = wireText,
                ["imagePath"] = imagePath,
                ["audioPath"] = audioPath,
                ["voiceSeconds"] = wireVoiceSeconds,
                ["elements"] = BuildElements(sent.Entities),
                ["time"] = new DateTimeOffset(DateTime.SpecifyKind(sent.Time, DateTimeKind.Utc)).ToString("o"),
                ["state"] = "Sent",
            };
            if (contentType == "Location")
            {
                wire["placeName"] = placeName;
                wire["address"] = address;
                wire["thumb"] = thumb;
            }
            if (!string.IsNullOrEmpty(replyToSender)) wire["replyToSender"] = replyToSender;
            if (!string.IsNullOrEmpty(replyToText)) wire["replyToText"] = replyToText;

            // For friend (C2C) sends, sent.Sequence is the random CLIENT sequence; register
            // it so the later server-sequence echo can be paired instead of double-recorded.
            var clientKey = kind == 'f' ? $"{conversationId}:cs:{sent.Sequence}" : null;
            lock (_gate)
            {
                if (!_messages.TryGetValue(conversationId, out var list)) { list = new(); _messages[conversationId] = list; }
                // The Tencent echo can beat this insert (OnBotMessage runs on a library event
                // thread). If it already recorded this message, return its copy instead of
                // adding a duplicate.
                if (clientKey != null && _clientSeqToId.TryGetValue(clientKey, out var echoId))
                {
                    var existing = list.FirstOrDefault(m => (string)m["id"]! == echoId);
                    if (existing != null) return (Clone(existing), null);
                }
                if (_rawMessages.ContainsKey(msgId))
                {
                    var existing = list.FirstOrDefault(m => (string)m["id"]! == msgId);
                    if (existing != null) return (Clone(existing), null);
                }
                list.Add(wire);
                _rawMessages[msgId] = sent;
                if (clientKey != null) _clientSeqToId[clientKey] = msgId;
            }
            BumpPreview(conversationId, PreviewFor((string)wire["contentType"]!, (string?)wire["text"]));
            Console.WriteLine($"[SendAsync] succeeded: {msgId} contentType={wireContentType}");

            return (wire, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SendAsync failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> ForwardAsync(string conversationId, string messageId)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        var kind = conversationId.Length >= 2 ? conversationId[0] : '\0';
        if ((kind != 'f' && kind != 'g') || !long.TryParse(conversationId.AsSpan(1), out var uin) || uin <= 0)
            return (null, "invalid-conversation");

        BotMessage? source;
        lock (_gate) { _rawMessages.TryGetValue(messageId, out source); }
        if (source == null) return (null, "message-not-found");

        try
        {
            var builder = new MessageBuilder();
            builder.MultiMsg(new List<BotMessage> { source });
            var chain = builder.Build();

            BotMessage sent = kind == 'g'
                ? await bot.SendGroupMessage(uin, chain)
                : await bot.SendFriendMessage(uin, chain);

            return (new JsonObject { ["id"] = sent.Sequence.ToString(), ["text"] = "[转发消息]" }, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> GroupRenameAsync(string conversationId, string newName)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return (null, "invalid-conversation");

        try
        {
            await bot.GroupRename(groupUin, newName);
            lock (_gate)
            {
                var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == conversationId);
                if (conv != null) conv["title"] = newName;
            }
            return (new JsonObject { ["renamed"] = true }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GroupRenameAsync({conversationId}) failed: {ex}");
            return (null, ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> GroupMemberRenameAsync(string conversationId, long targetUin, string newName)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return (null, "invalid-conversation");

        try
        {
            await bot.GroupMemberRename(groupUin, targetUin, newName);
            lock (_gate)
            {
                if (_groupMembers.TryGetValue(conversationId, out var members))
                {
                    var member = members.FirstOrDefault(m => (long)m["uin"]! == targetUin);
                    if (member != null) member["name"] = newName;
                }
            }
            return (new JsonObject { ["renamed"] = true }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GroupMemberRenameAsync({conversationId}) failed: {ex}");
            return (null, ex.Message);
        }
    }

    public async Task<(JsonObject? data, string? error)> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title)
    {
        var bot = _bot;
        if (bot == null) return (null, "not-online");
        if (conversationId.Length < 2 || conversationId[0] != 'g' || !long.TryParse(conversationId.AsSpan(1), out var groupUin))
            return (null, "invalid-conversation");

        try
        {
            await bot.GroupSetSpecialTitle(groupUin, targetUin, title);
            return (new JsonObject { ["set"] = true }, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GroupSetSpecialTitleAsync({conversationId}) failed: {ex}");
            return (null, ex.Message);
        }
    }

    // ---- helpers ----

    private void BumpPreview(string convId, string preview)
    {
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == convId);
            if (conv != null)
            {
                conv["preview"] = preview;
                conv["lastTime"] = DateTimeOffset.UtcNow.ToString("o");
            }
        }
    }

    private void BumpConversationOrCreate(string convId, string kind, string title, string avatarPath, string preview,
        bool incrementUnread = false)
    {
        lock (_gate)
        {
            var conv = _conversations.FirstOrDefault(c => (string)c["id"]! == convId);
            if (conv == null)
            {
                var row = new JsonObject
                {
                    ["id"] = convId,
                    ["kind"] = kind,
                    ["title"] = title,
                    ["avatarPath"] = avatarPath,
                    ["preview"] = preview,
                    ["lastTime"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["unread"] = incrementUnread ? 1 : 0,
                };
                ApplyPrefsTo(row);
                _conversations.Add(row);
            }
            else
            {
                conv["preview"] = preview;
                conv["lastTime"] = DateTimeOffset.UtcNow.ToString("o");
                if (incrementUnread)
                {
                    var cur = 0;
                    if (conv["unread"] is JsonValue jv) jv.TryGetValue<int>(out cur);
                    conv["unread"] = cur + 1;
                }
                else if (_convPrefs.TryGetValue(convId, out var pref) && pref.muted)
                {
                    conv["unread"] = 0;
                }
            }
        }
    }

    // ---- pin / mute prefs (local; not synced to Tencent) ----

    private static string PrefsPath(long uin) =>
        Path.Combine(AppContext.BaseDirectory, $"conv_prefs_{uin}.json");

    /// <summary>Caller must hold _gate (or be in a single-threaded setup path before any concurrent access).</summary>
    private void LoadPrefs(long uin)
    {
        _prefsUin = uin;
        _convPrefs.Clear();
        if (uin <= 0) return;
        try
        {
            var path = PrefsPath(uin);
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
            Console.WriteLine($"[prefs] loaded {_convPrefs.Count} entries for uin={uin}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] LoadPrefs failed: " + ex.Message);
        }
    }

    /// <summary>Caller must hold _gate.</summary>
    private void SavePrefs()
    {
        if (_prefsUin <= 0) return;
        try
        {
            var root = new JsonObject();
            foreach (var kv in _convPrefs)
            {
                if (!kv.Value.pinned && !kv.Value.muted) continue;
                root[kv.Key] = new JsonObject
                {
                    ["isPinned"] = kv.Value.pinned,
                    ["isMuted"] = kv.Value.muted,
                };
            }
            File.WriteAllText(PrefsPath(_prefsUin), root.ToJsonString());
        }
        catch (Exception ex)
        {
            Console.WriteLine("[!] SavePrefs failed: " + ex.Message);
        }
    }

    /// <summary>Stamp isPinned/isMuted onto a conversation row from _convPrefs. Caller holds _gate.</summary>
    private void ApplyPrefsTo(JsonObject conv)
    {
        var id = (string?)conv["id"] ?? "";
        if (_convPrefs.TryGetValue(id, out var p))
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

    private static bool IsTruthy(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string PreviewFor(string contentType, string? text) => contentType switch
    {
        "Image" => "[图片]",
        "Sticker" => "[表情]",
        "Voice" => "[语音]",
        "Video" => "[视频]",
        "FileMsg" => text ?? "[文件]",
        "Location" => text ?? "[位置]",
        _ => text ?? "",
    };

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    /// <summary>
    /// Builds our wire message id. Group (g) sends and receives both carry the real SERVER
    /// sequence (LagrangeV2's SendMessageService coalesces ClientSequence==0 back to Sequence
    /// for groups), so "g{groupUin}:{seq}" is unambiguous on its own.
    ///
    /// Friend (f) messages are NOT: OnBotMessage's incoming/echo path always sees the real
    /// SERVER sequence, but SendAsync's send response carries the random CLIENT sequence
    /// (10000-99999, see BotMessage.ClientSequence) -- the same numeric value can plausibly
    /// occur in both spaces, and both used to collide in the same "f{uin}:{seq}" key, silently
    /// dropping incoming messages or misrouting send-echoes onto the wrong entry. Tag C2C ids
    /// with which sequence space produced them ("s" = server, "c" = client) so they can never
    /// collide; the id is an opaque string to the client either way.
    /// </summary>
    private static string WireMessageId(string convId, ulong sequence, bool isServerSequence)
        => convId.Length > 0 && convId[0] == 'f'
            ? $"{convId}:{(isServerSequence ? 's' : 'c')}{sequence}"
            : $"{convId}:{sequence}";

    private static string FriendAvatarUrl(long uin) => $"https://q1.qlogo.cn/g?b=qq&nk={uin}&s=100";

    private static string GroupAvatarUrl(long groupUin) => $"https://p.qlogo.cn/gh/{groupUin}/{groupUin}/100";

    /// <summary>
    /// Maps a received MessageChain to (contentType, text, imagePath, voiceSeconds, replyToSender,
    /// replyToText). A chain that's a single ImageEntity gets promoted to contentType "Image"
    /// with the real CDN FileUrl (the app's Image/ImageBrush bindings already accept http(s)
    /// URIs natively, so this costs nothing extra client-side). A ReplyEntity is pulled out
    /// into its own two fields (not folded into the visible text) so the client renders a
    /// proper quote box instead of an inline "[回复 X]" prefix; the quoted text itself is
    /// looked up from our own already-recorded messages by the reply's source sequence.
    /// Everything else falls back to a text placeholder per entity -- voice/video/file/card/
    /// forwarded-chat aren't rendered richly this pass.
    /// </summary>
    private (string contentType, string? text, string? imagePath, string? audioPath, int voiceSeconds, string? replyToSender, string? replyToText) MapEntities(MessageChain chain, string convId)
    {
        if (chain.Count == 1 && chain[0] is ImageEntity img)
            return ("Image", img.ToPreviewString(), img.FileUrl, null, 0, null, null);

        // Multi-image only (no text): contentType Mixed so client uses element layout.
        var nonReplyEntities = chain.Where(e => e is not ReplyEntity).ToList();
        if (nonReplyEntities.Count > 1 && nonReplyEntities.All(e => e is ImageEntity))
        {
            var images = nonReplyEntities.Cast<ImageEntity>().ToList();
            var firstUrl = images[0].FileUrl;
            string? rSender = null, rText = null;
            foreach (var entity in chain)
            {
                if (entity is ReplyEntity reply)
                {
                    var (ms, mt) = FindMessageMeta(convId, WireMessageId(convId, reply.SrcSequence, isServerSequence: true));
                    rSender = string.IsNullOrEmpty(reply.Source?.Nickname) ? ms : reply.Source!.Nickname;
                    rText = mt;
                }
            }
            return ("Mixed", $"[图片×{images.Count}]", firstUrl, null, 0, rSender, rText);
        }

        // 图文混排: at least one Text/Mention and at least one Image in the same chain.
        var hasTextPart = nonReplyEntities.Any(e => e is TextEntity or MentionEntity);
        var hasImagePart = nonReplyEntities.Any(e => e is ImageEntity);
        if (hasTextPart && hasImagePart)
        {
            string? rSender = null, rText = null;
            var caption = new System.Text.StringBuilder();
            string? firstImg = null;
            foreach (var entity in chain)
            {
                if (entity is ReplyEntity reply)
                {
                    var (ms, mt) = FindMessageMeta(convId, WireMessageId(convId, reply.SrcSequence, isServerSequence: true));
                    rSender = string.IsNullOrEmpty(reply.Source?.Nickname) ? ms : reply.Source!.Nickname;
                    rText = mt;
                    continue;
                }
                if (entity is TextEntity te)
                {
                    if (caption.Length > 0) caption.Append(' ');
                    caption.Append(te.Text);
                }
                else if (entity is MentionEntity me)
                {
                    if (caption.Length > 0) caption.Append(' ');
                    caption.Append(me.Display ?? ("@" + me.Uin));
                }
                else if (entity is ImageEntity ie && firstImg == null)
                {
                    firstImg = ie.FileUrl;
                }
            }
            var cap = caption.ToString();
            if (string.IsNullOrWhiteSpace(cap)) cap = "[图片]";
            return ("Mixed", cap, firstImg, null, 0, rSender, rText);
        }

        // Video: also called out as its own contentType (like Image above) rather than folded
        // into the generic text fallback below, so the client can render a distinct video
        // bubble. voiceSeconds is reused to carry VideoLength (seconds) per the wire contract --
        // the direct playback URL is NOT resolved here (MapEntities is synchronous; that would
        // require an async GetNTV2RichMediaUrl call) -- the client fetches it on demand via the
        // separate getMediaUrl request, keyed off this message's wire id.
        if (chain.Count == 1 && chain[0] is VideoEntity vid)
            return ("Video", vid.ToPreviewString(), null, null, (int)vid.VideoLength, null, null);

        // Voice / PTT: promote to contentType Voice if chain contains a RecordEntity
        var recEntity = chain.OfType<RecordEntity>().FirstOrDefault();
        if (recEntity != null)
        {
            string? rSender = null, rText = null;
            foreach (var entity in chain)
            {
                if (entity is ReplyEntity reply)
                {
                    var (ms, mt) = FindMessageMeta(convId, WireMessageId(convId, reply.SrcSequence, isServerSequence: true));
                    rSender = string.IsNullOrEmpty(reply.Source?.Nickname) ? ms : reply.Source!.Nickname;
                    rText = mt;
                }
            }
            return ("Voice", recEntity.ToPreviewString(), null, recEntity.FileUrl, (int)recEntity.RecordLength, rSender, rText);
        }

        // Single GroupFileEntity: promote to FileMsg; fileId/name/size stamped on wire after MapEntities.
        if (chain.Count == 1 && chain[0] is GroupFileEntity gf)
            return ("FileMsg", $"[群文件] {gf.FileName}", null, null, 0, null, null);

        string? replyToSender = null;
        string? replyToText = null;
        var sb = new System.Text.StringBuilder();
        var voiceSeconds = 0;
        foreach (var entity in chain)
        {
            if (entity is ReplyEntity reply)
            {
                // reply.Source resolves via the friend list, which misses for our own
                // messages (a friend replying to something WE sent) -- fall back to the
                // recorded wire snapshot's senderName in that case. reply.SrcSequence is
                // always the SERVER sequence (it comes straight off the wire).
                var (metaSender, metaText) = FindMessageMeta(convId, WireMessageId(convId, reply.SrcSequence, isServerSequence: true));
                replyToSender = string.IsNullOrEmpty(reply.Source?.Nickname) ? metaSender : reply.Source!.Nickname;
                replyToText = metaText;
                continue;
            }
            if (entity is RecordEntity rec) voiceSeconds = (int)rec.RecordLength;
            var piece = EntityToText(entity);
            if (string.IsNullOrEmpty(piece)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(piece);
        }
        return ("Text", sb.ToString(), null, null, voiceSeconds, replyToSender, replyToText);
    }

    /// <summary>Look up the senderName and text of an already-recorded message by wire id,
    /// for reconstructing a reply-quote header consistent with how the message displays.</summary>
    private (string? sender, string? text) FindMessageMeta(string convId, string wireId)
    {
        lock (_gate)
        {
            if (_messages.TryGetValue(convId, out var list))
            {
                var match = list.FirstOrDefault(m => (string)m["id"]! == wireId);
                if (match != null) return ((string?)match["senderName"], (string?)match["text"]);
            }
        }
        return (null, null);
    }

    private static string EntityToText(IMessageEntity entity) => entity switch
    {
        TextEntity t => t.Text,
        MentionEntity m => m.Display ?? ("@" + m.Uin),
        ImageEntity img => img.ToPreviewString(),
        RecordEntity rec => rec.ToPreviewString(),
        VideoEntity vid => vid.ToPreviewString(),
        GroupFileEntity f => $"[群文件] {f.FileName}",
        LightAppEntity la => $"[卡片消息] {la.AppName}",
        MultiMsgEntity => "[聊天记录]",
        _ => "",
    };

    private static JsonArray BuildElements(MessageChain chain)
    {
        var elements = new JsonArray();
        foreach (var entity in chain)
        {
            if (entity is TextEntity t) elements.Add(new JsonObject { ["Type"] = "Text", ["Text"] = t.Text });
            else if (entity is MentionEntity m) elements.Add(new JsonObject { ["Type"] = "Mention", ["Text"] = m.Display ?? ("@" + m.Uin), ["Uin"] = m.Uin });
            else if (entity is ImageEntity img) elements.Add(new JsonObject { ["Type"] = "Image", ["Url"] = img.FileUrl });
            else if (entity is RecordEntity rec) elements.Add(new JsonObject { ["Type"] = "Record", ["Url"] = rec.FileUrl });
            else if (entity is VideoEntity vid) elements.Add(new JsonObject { ["Type"] = "Video", ["Url"] = vid.FileUrl });
            else if (entity is GroupFileEntity f) elements.Add(new JsonObject { ["Type"] = "File", ["Text"] = f.FileName });
            else if (entity is ReplyEntity) continue; // Replies handled separately
            else elements.Add(new JsonObject { ["Type"] = "Text", ["Text"] = EntityToText(entity) });
        }
        return elements;
    }

    private static JsonObject Clone(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    // ---- QQ 空间 native feed fetch ----

    /// <summary>Extract skey from keystore as lowercase hex — used for QQ web API cookie auth.</summary>
    private static string GetSkey(BotContext bot)
    {
        // skey is a printable cookie string, not a hex blob.
        var bytes = bot.Keystore.WLoginSigs.SKey;
        if (bytes is not { Length: > 0 }) return "";
        try { return System.Text.Encoding.ASCII.GetString(bytes); }
        catch { return ""; }
    }

    /// <summary>Hash33 / g_tk CSRF token from pskey, required by QQ web APIs (qzone, etc.).</summary>
    private static int CalculateGtk(string skey)
    {
        var hash = 5381;
        foreach (var ch in skey)
            hash += (hash << 5) + ch;
        return hash & 0x7fffffff;
    }



    /// <summary>
    /// Best-effort extraction of friend feeds from feeds3_html_more JS payload
    /// when full JSON parse fails (bare keys + single quotes + mixed HTML).
    /// </summary>
    private static List<JsonObject> TryParseFeeds3Loose(string bodyText)
    {
        var list = new List<JsonObject>();
        if (string.IsNullOrEmpty(bodyText)) return list;

        string DecodeQx(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // \xHH sequences from HTML attributes
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\x([0-9A-Fa-f]{2})", m =>
            {
                try { return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(); }
                catch { return m.Value; }
            });
            // \uXXXX sequences
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9A-Fa-f]{4})", m =>
            {
                try { return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(); }
                catch { return m.Value; }
            });
            s = System.Net.WebUtility.HtmlDecode(s);
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
            return s;
        }

        // Prefer larger dumps if available.
        var parts = bodyText.Split(new[] { "ver:'1'", "ver:\"1\"" }, StringSplitOptions.None);
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];

            string Grab(string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(part, key + @":'([^']*)'");
                if (m.Success) return m.Groups[1].Value;
                m = System.Text.RegularExpressions.Regex.Match(part, key + ":\\\"([^\\\"]*)\\\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            long GrabLong(string key)
            {
                var s = Grab(key);
                if (long.TryParse(s, out var v)) return v;
                var m = System.Text.RegularExpressions.Regex.Match(part, key + @":(\d+)");
                return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : 0;
            }

            var key = Grab("key");
            var nickname = DecodeQx(Grab("nickname"));
            if (string.IsNullOrEmpty(nickname)) nickname = DecodeQx(Grab("remark"));
            if (string.IsNullOrEmpty(nickname)) nickname = DecodeQx(Grab("qzonename"));

            var uin = GrabLong("opuin");
            if (uin == 0) uin = GrabLong("uin");
            // Avoid mistaking host/self fields when multiple uin appear; prefer opuin already.

            var summary = DecodeQx(Grab("summary"));
            if (string.IsNullOrEmpty(summary)) summary = DecodeQx(Grab("title"));
            if (string.IsNullOrEmpty(summary)) summary = DecodeQx(Grab("summaryTemp"));
            if (string.IsNullOrEmpty(summary)) summary = DecodeQx(Grab("feedstitle"));
            if (string.IsNullOrEmpty(summary)) summary = DecodeQx(Grab("sharetxt"));
            if (string.IsNullOrEmpty(summary)) summary = DecodeQx(Grab("content"));

            // feeds3 often puts the real post body only inside the embedded html fragment.
            if (string.IsNullOrEmpty(summary))
            {
                var html = Grab("html");
                if (!string.IsNullOrEmpty(html))
                {
                    var decoded = DecodeQx(html);
                    var tm = System.Text.RegularExpressions.Regex.Match(decoded, @"f-single-txt[\s\S]*?>([\s\S]*?)</div>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (tm.Success) summary = DecodeQx(tm.Groups[1].Value);
                    if (string.IsNullOrEmpty(summary))
                    {
                        var plain = System.Text.RegularExpressions.Regex.Replace(decoded, "<[^>]+>", " ");
                        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim();
                        if (plain.Length > 120) plain = plain[..120];
                        if (plain.Length >= 2 && !plain.StartsWith("http")) summary = plain;
                    }
                }
            }

            var abstime = GrabLong("abstime");
            var appid = Grab("appid");

            // Ads / system cards
            if (!string.IsNullOrEmpty(key) && key.StartsWith("advertisement", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Contains("outlink", StringComparison.OrdinalIgnoreCase))
                continue;
            if (uin == 0 && (appid == "0" || appid == "3110"))
                continue;
            if (nickname.Contains("官方Qzone") || nickname.Equals("官方Qzone", StringComparison.Ordinal))
                continue;

            if (string.IsNullOrEmpty(key) && uin == 0 && string.IsNullOrEmpty(summary) && string.IsNullOrEmpty(nickname))
                continue;

            var createTime = "";
            if (abstime > 0)
            {
                if (abstime > 10_000_000_000) abstime /= 1000;
                try { createTime = DateTimeOffset.FromUnixTimeSeconds(abstime).ToString("o"); } catch { }
            }
            if (string.IsNullOrEmpty(key)) key = $"{uin}_{abstime}";
            if (string.IsNullOrEmpty(nickname)) nickname = uin > 0 ? uin.ToString() : "QQ好友";

            // Always use stable QQ avatar CDN, ignore fragile qlogo*.store.qq.com links.
            var avatar = uin > 0 ? FriendAvatarUrl(uin) : "";

            // Keep empty text posts if we at least have a friend identity.
            list.Add(new JsonObject
            {
                ["id"] = key,
                ["authorName"] = nickname,
                ["authorAvatarPath"] = avatar,
                ["text"] = summary ?? "",
                ["timeText"] = FormatSpaceTime(createTime),
                ["time"] = createTime,
                ["images"] = new JsonArray(),
                ["videoPath"] = "",
                ["likeCount"] = 0,
                ["isLiked"] = false,
                ["comments"] = new JsonArray(),
            });
        }
        return list;
    }

    
    private static List<JsonObject> ExtractFeeds3WithPython(string bodyText)
    {
        var list = new List<JsonObject>();
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "extract_feeds3.py"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_reverse", "extract_feeds3.py")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "_reverse", "extract_feeds3.py")),
            };
            string? script = null;
            foreach (var c in candidates) if (File.Exists(c)) { script = c; break; }
            if (script == null)
            {
                Console.WriteLine("[QzoneFeed] extract_feeds3.py not found");
                return list;
            }

            var tmpIn = Path.Combine(Path.GetTempPath(), "qzone_feeds3_in_" + Guid.NewGuid().ToString("N") + ".txt");
            var tmpOut = Path.Combine(Path.GetTempPath(), "qzone_feeds3_out_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tmpIn, bodyText);
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                ArgumentList = { script, tmpIn, tmpOut },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return list;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(20000))
            {
                try { proc.Kill(true); } catch { }
                Console.WriteLine("[QzoneFeed] python extract timeout");
            }
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.WriteLine("[QzoneFeed] python extract stderr: " + stderr.Trim());
            if (File.Exists(tmpOut))
            {
                var json = File.ReadAllText(tmpOut);
                if (JsonNode.Parse(json) is JsonArray arr)
                {
                    foreach (var n in arr)
                        if (n is JsonObject o) list.Add(o);
                }
                try { File.Delete(tmpOut); } catch { }
            }
            try { File.Delete(tmpIn); } catch { }
            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine("[QzoneFeed] python extract: " + stdout.Trim());
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QzoneFeed] python extract failed: " + ex.Message);
        }
        return list;
    }

private static string? NormalizeQzonePayload(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return null;
        var t = bodyText.Trim();

        foreach (var wrap in new[] { "_Callback", "_preloadCallback", "callback", "shine0_Callback", "feedCallback" })
        {
            var prefix = wrap + "(";
            if (t.StartsWith(prefix, StringComparison.Ordinal) && t.EndsWith(")"))
            {
                t = t[prefix.Length..^1].Trim();
                break;
            }
            // Some dumps are truncated and do not end with ')'
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                t = t[prefix.Length..].Trim();
                if (t.EndsWith(")")) t = t[..^1].Trim();
                break;
            }
        }
        if (t.StartsWith("while(1);")) t = t["while(1);".Length..].Trim();

        // Prefer the inner object after "data": for feeds3_html_more.
        // Outer shell is valid-ish JSON, but value of data is a JS object: {main:{...},data:[...]}
        var dataKey = t.IndexOf("\"data\"", StringComparison.Ordinal);
        if (dataKey < 0) dataKey = t.IndexOf("\n{main:", StringComparison.Ordinal);
        if (dataKey >= 0)
        {
            var brace = t.IndexOf('{', dataKey);
            // For pattern `"data":\n{main:` the first { after data is the JS object we want.
            // But code/message also contain braces earlier; ensure we jump near data.
            if (brace > dataKey)
            {
                // If we matched `"data"`, find ':' then object.
                var colon = t.IndexOf(':', dataKey);
                if (colon > 0)
                {
                    var b2 = t.IndexOf('{', colon);
                    if (b2 > 0) t = t[b2..].Trim();
                }
            }
        }

        // Convert single-quoted strings to JSON strings.
        var sb = new System.Text.StringBuilder(t.Length + 64);
        for (var i = 0; i < t.Length; )
        {
            var ch = t[i];
            if (ch == '\'')
            {
                i++;
                var content = new System.Text.StringBuilder();
                while (i < t.Length)
                {
                    var c = t[i];
                    if (c == '\\' && i + 1 < t.Length)
                    {
                        content.Append(t[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '\'') { i++; break; }
                    content.Append(c);
                    i++;
                }
                sb.Append(System.Text.Json.JsonSerializer.Serialize(content.ToString()));
                continue;
            }
            sb.Append(ch);
            i++;
        }
        t = sb.ToString();

        // Quote bare identifiers used as object keys.
        t = System.Text.RegularExpressions.Regex.Replace(
            t,
            @"(?<=[{\[,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:",
            "\"$1\":");
        // Also quote keys at line starts inside objects.
        t = System.Text.RegularExpressions.Regex.Replace(
            t,
            @"(?m)(?<=[{,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:",
            "\"$1\":");

        // Remove trailing commas before } or ]
        t = System.Text.RegularExpressions.Regex.Replace(t, @",(\s*[}\]])", "$1");

        // If payload is the inner {main:...,data:[...]}, wrap as {"data": ...}
        if ((t.Contains("\"main\"") || t.Contains("\"data\"")) && !t.Contains("\"code\""))
        {
            t = "{\"code\":0,\"data\":" + t + "}";
        }
        return t;
    }

    /// <summary>Fetch one page of QQ 空间好友动态. Returns (added, hasMore).</summary>
    private async Task<(int added, bool hasMore)> FetchQzoneFeedPageAsync(int pos, int num)
    {
        var bot = _bot;
        if (bot == null) return (0, false);

        Dictionary<string, string> cookies;
        try
        {
            cookies = await bot.FetchCookies(new List<string> { "qzone.qq.com" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QzoneFeed] FetchCookies failed: {ex.Message}");
            return (0, false);
        }

        if (!cookies.TryGetValue("qzone.qq.com", out var pskey) || string.IsNullOrEmpty(pskey))
        {
            Console.WriteLine("[QzoneFeed] no pskey for qzone.qq.com — service ticket not available");
            return (0, false);
        }

        var gtk = CalculateGtk(pskey);
        var skey = GetSkey(bot);
        var uin = bot.BotUin;
        Console.WriteLine($"[QzoneFeed] fetching page pos={pos} num={num} uin={uin} auth=pskey{(string.IsNullOrEmpty(skey) ? "" : "+skey")}");

        var cookie = $"uin=o{uin}; p_uin=o{uin}; p_skey={pskey}";
        if (!string.IsNullOrEmpty(skey)) cookie += $"; skey={skey}";

        // Friend/active feed first. res_type=2 + own uin tends to return only self moods.
        // feeds3_html_more / mfeeds_get_active_feeds are the classic "好友动态" surfaces.
        var attach = pos > 0 ? $"offset%3D{pos}" : "";
        var candidates = new[]
        {
            // mobile friend/active feeds
            $"https://mobile.qzone.qq.com/feeds/mfeeds_get_active_feeds?format=json&g_tk={gtk}&count={num}&attachinfo={attach}",
            $"https://mobile.qzone.qq.com/get_feeds?res_type=0&res_attach={attach}&refresh_type=1&format=json&g_tk={gtk}&count={num}",
            // h5 friend feed list
            $"https://h5.qzone.qq.com/proxy/domain/ic2.qzone.qq.com/cgi-bin/feeds/feeds3_html_more?uin={uin}&scope=0&view=1&daylist=&uinlist=&filter=all&flag=1&applist=all&refresh=0&begintime=0&format=jsonp&g_tk={gtk}&useutf8=1&outputhtmlfeed=0&callback=_Callback",
            // keep self mood list as last fallback only
            $"https://h5.qzone.qq.com/proxy/domain/taotao.qzone.qq.com/cgi-bin/emotion_cgi_msglist_v6?uin={uin}&ftype=0&sort=0&pos={pos}&num={num}&replynum=100&g_tk={gtk}&code_version=1&format=json&need_private_comment=1"
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 13; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 QQ/9.0.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://h5.qzone.qq.com/");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://h5.qzone.qq.com");

        string? respText = null;
        string used = "";
        foreach (var url in candidates)
        {
            try
            {
                using var respMsg = await http.GetAsync(url);
                var bodyText = await respMsg.Content.ReadAsStringAsync();
                Console.WriteLine($"[QzoneFeed] try {url.Split('?')[0]} -> HTTP {(int)respMsg.StatusCode} len={bodyText.Length}");
                if (url.Contains("feeds3_html_more"))
                {
                    try
                    {
                        var fullDump = Path.Combine(AppContext.BaseDirectory, "qzone_feeds3_full.txt");
                        File.WriteAllText(fullDump, bodyText);
                        Console.WriteLine("[QzoneFeed] dumped full feeds3 to " + fullDump + " size=" + bodyText.Length);
                    }
                    catch (Exception dumpEx) { Console.WriteLine("[QzoneFeed] full dump failed: " + dumpEx.Message); }
                }
                if (!respMsg.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[QzoneFeed] body: {bodyText[..Math.Min(160, bodyText.Length)]}");
                    continue;
                }

                var t = NormalizeQzonePayload(bodyText);
                if (string.IsNullOrWhiteSpace(t))
                {
                    Console.WriteLine("[QzoneFeed] empty payload after normalize");
                    continue;
                }

                JsonNode? probe = null;
                try { probe = JsonNode.Parse(t); }
                catch (Exception parseEx)
                {
                    // feeds3_html_more is often not pure JSON; extract cards loosely.
                    var loose = ExtractFeeds3WithPython(bodyText);
                    if (loose.Count == 0) loose = TryParseFeeds3Loose(bodyText);
                    if (loose.Count > 0)
                    {
                        Console.WriteLine($"[QzoneFeed] loose-parse recovered {loose.Count} feeds from {url.Split('?')[0]} ({parseEx.Message})");
                        var addedLoose = 0;
                        lock (_gate)
                        {
                            foreach (var item in loose)
                            {
                                var feedKey = (string?)item["id"] ?? Guid.NewGuid().ToString("N");
                                _spaceFeed.RemoveAll(x => (string?)x["id"] == feedKey);
                                _spaceFeed.Insert(_spaceFeed.Count, item);
                                addedLoose++;
                            }
                            while (_spaceFeed.Count > MaxSpaceFeedItems)
                                _spaceFeed.RemoveAt(_spaceFeed.Count - 1);
                        }
                        Console.WriteLine($"[QzoneFeed] ingested {addedLoose} feeds via loose:{url.Split('?')[0]} (total={_spaceFeed.Count})");
                        return (addedLoose, true);
                    }

                    try
                    {
                        var dump = Path.Combine(AppContext.BaseDirectory, "qzone_last_raw.txt");
                        // Keep a larger sample for offline diagnosis.
                        File.WriteAllText(dump, bodyText.Length > 300000 ? bodyText[..300000] : bodyText);
                        Console.WriteLine($"[QzoneFeed] parse failed ({parseEx.Message}); dumped {dump}");
                    }
                    catch { Console.WriteLine($"[QzoneFeed] parse failed: {parseEx.Message}"); }
                    continue;
                }
                if (probe == null) continue;
                int code = -1;
                if (probe["code"] is JsonValue cv)
                {
                    if (cv.TryGetValue<int>(out var ci)) code = ci;
                    else if (cv.TryGetValue<double>(out var cd)) code = (int)cd;
                    else if (cv.TryGetValue<string>(out var cs) && int.TryParse(cs, out var cp)) code = cp;
                }
                else if (probe["ret"] is JsonValue rv)
                {
                    if (rv.TryGetValue<int>(out var ri)) code = ri;
                    else if (rv.TryGetValue<double>(out var rd)) code = (int)rd;
                    else if (rv.TryGetValue<string>(out var rs) && int.TryParse(rs, out var rp)) code = rp;
                }
                if (code != 0 && code != 200)
                {
                    var msg = probe["message"]?.GetValue<string>() ?? probe["msg"]?.GetValue<string>() ?? "unknown";
                    Console.WriteLine($"[QzoneFeed] API code={code} message={msg}");
                    continue;
                }

                respText = t;
                used = url.Split('?')[0];
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QzoneFeed] request failed: {ex.Message}");
            }
        }

        if (respText == null)
        {
            Console.WriteLine("[QzoneFeed] all endpoints failed");
            return (0, false);
        }

        var root = JsonNode.Parse(respText);
        if (root == null)
        {
            Console.WriteLine("[QzoneFeed] empty/invalid response");
            return (0, false);
        }

        JsonArray? feedList = null;
        JsonNode? data = root["data"] ?? root["result"] ?? root;
        feedList = data?["feedlist"] as JsonArray
            ?? data?["feeds"] as JsonArray
            ?? data?["vFeeds"] as JsonArray
            ?? data?["msglist"] as JsonArray
            ?? data?["items"] as JsonArray
            ?? data?["data"] as JsonArray
            ?? root["msglist"] as JsonArray
            ?? root["feeds"] as JsonArray
            ?? root["vFeeds"] as JsonArray;
        if (feedList == null || feedList.Count == 0)
        {
            var nested = data?["data"] ?? root["data"];
            feedList = nested?["vFeeds"] as JsonArray
                ?? nested?["feeds"] as JsonArray
                ?? nested?["feedlist"] as JsonArray
                ?? nested?["items"] as JsonArray
                ?? nested?["singlefeed"] as JsonArray;
        }
        // feeds3_html_more sometimes returns data as object map; flatten values that look like feeds
        if ((feedList == null || feedList.Count == 0) && data is JsonObject dataObj)
        {
            var flattened = new JsonArray();
            foreach (var kv in dataObj)
            {
                if (kv.Value is JsonObject fo && (fo["comm"] != null || fo["userinfo"] != null || fo["summary"] != null))
                    flattened.Add(fo);
                else if (kv.Value is JsonArray arr)
                {
                    foreach (var n in arr)
                        if (n is JsonObject no && (no["comm"] != null || no["userinfo"] != null || no["summary"] != null))
                            flattened.Add(no);
                }
            }
            if (flattened.Count > 0) feedList = flattened;
        }
        if (feedList == null || feedList.Count == 0)
        {
            // Some feeds3 responses normalize into an object without array; try loose extract on original raw if available.
            Console.WriteLine($"[QzoneFeed] feedlist is empty via {used}");
            return (0, false);
        }

        Console.WriteLine($"[QzoneFeed] parsing {feedList.Count} items via {used}");
        try
        {
            var first = feedList[0];
            if (first is JsonObject fo)
            {
                Console.WriteLine($"[QzoneFeed] first keys={string.Join(',', fo.Select(kv => kv.Key))}");
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "qzone_first_feed.json");
                    File.WriteAllText(path, fo.ToJsonString());
                    Console.WriteLine("[QzoneFeed] dumped first feed to " + path);
                }
                catch (Exception dumpEx) { Console.WriteLine("[QzoneFeed] dump file failed: " + dumpEx.Message); }
            }
            else Console.WriteLine($"[QzoneFeed] first node type={first?.GetType().Name}");
        }
        catch (Exception ex) { Console.WriteLine("[QzoneFeed] dump first failed: " + ex.Message); }
        var hasMore = true;
        if (data?["hasmore"] is JsonValue hmv && hmv.TryGetValue<bool>(out var hm)) hasMore = hm;
        else if (data?["has_more"] is JsonValue hmv2 && hmv2.TryGetValue<bool>(out var hm2)) hasMore = hm2;
        else if (feedList.Count < num) hasMore = false;

        var added = 0;
        foreach (var feedNode in feedList)
        {
            try
            {
            if (feedNode is not JsonObject feed)
            {
                Console.WriteLine($"[QzoneFeed] skip non-object feed node type={feedNode?.GetType().Name}");
                continue;
            }

            static string ReadStr(JsonNode? n)
            {
                if (n is null) return "";
                if (n is JsonValue jv)
                {
                    if (jv.TryGetValue<string>(out var s) && s != null) return s;
                    if (jv.TryGetValue<long>(out var l)) return l.ToString();
                    if (jv.TryGetValue<double>(out var d)) return ((long)d).ToString();
                    if (jv.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                    return jv.ToJsonString().Trim('"');
                }
                if (n is JsonObject jo)
                {
                    if (jo["summary"] is JsonNode sn) return ReadStr(sn);
                    if (jo["content"] is JsonNode cn) return ReadStr(cn);
                    if (jo["nickname"] is JsonNode nn) return ReadStr(nn);
                    if (jo["name"] is JsonNode nm) return ReadStr(nm);
                    if (jo["uin"] is JsonNode un) return ReadStr(un);
                    if (jo["cellid"] is JsonNode cid) return ReadStr(cid);
                    if (jo["user"] is JsonNode usr) return ReadStr(usr);
                    if (jo["title"] is JsonNode tn) return ReadStr(tn);
                    if (jo["text"] is JsonNode xn) return ReadStr(xn);
                }
                return "";
            }
            static long ReadLongNode(JsonNode? n)
            {
                if (n is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out var l)) return l;
                    if (jv.TryGetValue<double>(out var d)) return (long)d;
                    if (jv.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
                }
                return 0;
            }
            static int ReadIntNode(JsonNode? n)
            {
                if (n is JsonValue jv)
                {
                    if (jv.TryGetValue<int>(out var i)) return i;
                    if (jv.TryGetValue<long>(out var l)) return (int)l;
                    if (jv.TryGetValue<double>(out var d)) return (int)d;
                    if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
                }
                return 0;
            }

            var comm = feed["comm"] as JsonObject;
            var userinfo = feed["userinfo"] as JsonObject;
            var user = userinfo?["user"] as JsonObject ?? userinfo;
            var likeNode = feed["like"] as JsonObject;
            var commentNode = feed["comment"] as JsonObject;
            var idNode = feed["id"];

            var feedKey = ReadStr(comm?["feedskey"]);
            if (string.IsNullOrEmpty(feedKey)) feedKey = ReadStr(feed["key"]);
            if (string.IsNullOrEmpty(feedKey)) feedKey = ReadStr(idNode);
            if (string.IsNullOrEmpty(feedKey)) feedKey = ReadStr(comm?["curlikekey"]);
            if (string.IsNullOrEmpty(feedKey)) feedKey = Guid.NewGuid().ToString("N");

            var text = ReadStr(feed["summary"]);
            if (string.IsNullOrEmpty(text)) text = ReadStr(feed["summaryTemp"]);
            if (string.IsNullOrEmpty(text)) text = ReadStr(feed["title"]);
            if (string.IsNullOrEmpty(text)) text = ReadStr(feed["content"]);
            if (string.IsNullOrEmpty(text)) text = ReadStr(feed["operation"]);

            var authorName = ReadStr(user?["nickname"]);
            if (string.IsNullOrEmpty(authorName)) authorName = ReadStr(user?["name"]);
            // feeds3_html_more friend cards use top-level nickname/opuin/logimg
            if (string.IsNullOrEmpty(authorName)) authorName = ReadStr(feed["nickname"]);
            if (string.IsNullOrEmpty(authorName)) authorName = ReadStr(feed["remark"]);
            if (string.IsNullOrEmpty(authorName)) authorName = "QQ好友";

            var authorUin = ReadLongNode(user?["uin"]);
            if (authorUin == 0) authorUin = ReadLongNode(feed["uin"]);
            if (authorUin == 0) authorUin = ReadLongNode(feed["opuin"]);
            var authorAvatar = ReadStr(user?["logo"]);
            if (string.IsNullOrEmpty(authorAvatar)) authorAvatar = ReadStr(feed["logimg"]);
            if (string.IsNullOrEmpty(authorAvatar) && authorUin > 0)
                authorAvatar = FriendAvatarUrl(authorUin);

            var createTime = "";
            var ts = ReadLongNode(comm?["time"]);
            if (ts <= 0) ts = ReadLongNode(feed["time"]);
            if (ts <= 0) ts = ReadLongNode(feed["abstime"]);
            if (ts > 0)
            {
                if (ts > 10_000_000_000) ts /= 1000;
                try { createTime = DateTimeOffset.FromUnixTimeSeconds(ts).ToString("o"); } catch { }
            }

            var images = new JsonArray();
            JsonArray? pic = null;
            if (feed["pic"] is JsonArray a1) pic = a1;
            else if (feed["images"] is JsonArray a2) pic = a2;
            else if (feed["cell_pic"]?["pic_list"] is JsonArray a3) pic = a3;
            else if (feed["picdata"]?["pic"] is JsonArray a4) pic = a4;
            else if (feed["operation"]?["pic"] is JsonArray a5) pic = a5;
            if (pic != null)
            {
                foreach (var pnode in pic)
                {
                    if (pnode is JsonObject po)
                    {
                        var url = ReadStr(po["url2"]);
                        if (string.IsNullOrEmpty(url)) url = ReadStr(po["url1"]);
                        if (string.IsNullOrEmpty(url)) url = ReadStr(po["url"]);
                        if (string.IsNullOrEmpty(url)) url = ReadStr(po["origin_url"]);
                        if (string.IsNullOrEmpty(url)) url = ReadStr(po["smallurl"]);
                        if (!string.IsNullOrEmpty(url)) images.Add(url);
                    }
                    else
                    {
                        var su = ReadStr(pnode);
                        if (!string.IsNullOrEmpty(su)) images.Add(su);
                    }
                }
            }

            int likeCount = 0;
            if (likeNode != null)
            {
                likeCount = ReadIntNode(likeNode["num"]);
                if (likeCount == 0) likeCount = ReadIntNode(likeNode["like_num"]);
                if (likeCount == 0) likeCount = ReadIntNode(likeNode["cntnum"]);
                if (likeCount == 0 && likeNode["likemans"] is JsonArray lm) likeCount = lm.Count;
            }

            var comments = new JsonArray();
            var commentArr = commentNode?["comments"] as JsonArray;
            if (commentArr != null)
            {
                foreach (var c in commentArr)
                {
                    if (c is not JsonObject co) continue;
                    var cText = ReadStr(co["content"]);
                    if (string.IsNullOrWhiteSpace(cText)) continue;
                    var cUser = co["user"] as JsonObject;
                    var cAuthor = ReadStr(cUser?["nickname"]);
                    if (string.IsNullOrEmpty(cAuthor)) cAuthor = ReadStr(cUser?["name"]);
                    if (string.IsNullOrEmpty(cAuthor)) cAuthor = "用户";
                    comments.Add(new JsonObject
                    {
                        ["author"] = cAuthor,
                        ["text"] = cText,
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(text) && images.Count > 0) text = "[图片]";

            var item = new JsonObject
            {
                ["id"] = feedKey,
                ["authorName"] = authorName,
                ["authorAvatarPath"] = authorAvatar,
                ["text"] = text ?? "",
                ["timeText"] = FormatSpaceTime(createTime),
                ["time"] = createTime,
                ["images"] = images,
                ["videoPath"] = "",
                ["likeCount"] = likeCount,
                ["isLiked"] = false,
                ["comments"] = comments,
            };

            lock (_gate)
            {
                _spaceFeed.RemoveAll(x => ReadStr(x["id"]) == feedKey);
                _spaceFeed.Insert(_spaceFeed.Count, item);
                while (_spaceFeed.Count > MaxSpaceFeedItems)
                    _spaceFeed.RemoveAt(_spaceFeed.Count - 1);
            }
            added++;
            }
            catch (Exception exFeed)
            {
                Console.WriteLine($"[QzoneFeed] item parse failed: {exFeed.GetType().Name}: {exFeed.Message}");
            }
        }

        Console.WriteLine($"[QzoneFeed] ingested {added} feeds via {used} (pos={pos}, total={_spaceFeed.Count}, hasMore={hasMore})");
        return (added, hasMore);
    }

    public async Task FetchQzoneFeedNativeAsync()
    {
        try
        {
            // Avoid hammering QQ Zone (WAF / "使用人数过多").
            if (DateTime.UtcNow - _spaceFeedLastFetchUtc < SpaceFeedMinInterval)
            {
                Console.WriteLine("[QzoneFeed] skip refresh: cooldown");
                return;
            }
            _spaceFeedLastFetchUtc = DateTime.UtcNow;

            var (added, hasMore) = await FetchQzoneFeedPageAsync(0, 20);
            lock (_gate)
            {
                // Do not rewind the history cursor if the client already paged past the
                // first window — otherwise "加载更多" keeps re-fetching page 0 after a
                // background getMoments refresh.
                if (_spaceFeedPos <= 20)
                {
                    _spaceFeedPos = Math.Max(20, added > 0 ? added : 20);
                    _spaceFeedHasMore = hasMore;
                }
            }
            if (added > 0)
            {
                Broadcast?.Invoke(new JsonObject
                {
                    ["type"] = "spaceFeedUpdated",
                    ["data"] = new JsonObject { ["added"] = added, ["source"] = "native", ["hasMore"] = _spaceFeedHasMore },
                }.ToJsonString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QzoneFeed] error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the NEXT page of QQ 空间动态 (older posts). Safe to call repeatedly;
    /// returns immediately with hasMore=false when there are no more pages.
    /// Merges into <see cref="_spaceFeed"/> and broadcasts <c>spaceFeedUpdated</c>.
    /// Returns a result object with <c>added</c> and <c>hasMore</c> so the client
    /// knows whether to offer "load more" again.
    /// </summary>
    public async Task<JsonObject> FetchEarlierSpaceFeedAsync(int num = 20)
    {
        if (num <= 0) num = 20;
        int pos;
        lock (_gate)
        {
            if (!_spaceFeedHasMore)
                return new JsonObject { ["added"] = 0, ["hasMore"] = false };
            // If first page was never fetched, start from 0 so load-more still works
            // when the user opens 动态 and immediately taps 加载更多.
            pos = _spaceFeedPos > 0 ? _spaceFeedPos : 0;
        }

        try
        {
            // Don't share the native-refresh cooldown; history paging is explicit user action.
            var (added, hasMore) = await FetchQzoneFeedPageAsync(pos, num);
            lock (_gate)
            {
                if (added > 0)
                {
                    // Advance by requested page size (not only unique adds) so offsets
                    // keep moving when the remote page partially overlaps the cache.
                    _spaceFeedPos = pos + Math.Max(added, num);
                    _spaceFeedHasMore = hasMore;
                }
                else
                {
                    // Zero new items usually means the endpoint has no further unique
                    // history (or does not support offset) — stop offering load-more.
                    _spaceFeedHasMore = false;
                    hasMore = false;
                }
            }

            if (added > 0)
            {
                Broadcast?.Invoke(new JsonObject
                {
                    ["type"] = "spaceFeedUpdated",
                    ["data"] = new JsonObject { ["added"] = added, ["source"] = "earlier", ["hasMore"] = hasMore },
                }.ToJsonString());
            }

            return new JsonObject { ["added"] = added, ["hasMore"] = hasMore };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QzoneFeed] earlier page error: {ex.GetType().Name}: {ex.Message}");
            return new JsonObject { ["added"] = 0, ["hasMore"] = false, ["error"] = ex.Message };
        }
    }

    // ---- debug: channel cookie helper ----

    public async Task<JsonObject?> GetChannelCookiesAsync()
    {
        var bot = _bot;
        if (bot == null) return new JsonObject { ["error"] = "bot-not-online" };

        try
        {
            // Fetch pskey for pd.qq.com via OIDB 0x102a
            var cookies = await bot.FetchCookies(new List<string> { "pd.qq.com" });
            var skeyBytes = bot.Keystore.WLoginSigs.SKey;
            var skey = skeyBytes is { Length: > 0 } ? Convert.ToHexString(skeyBytes).ToLowerInvariant() : "";

            var result = new JsonObject
            {
                ["uin"] = bot.BotUin,
                ["skey"] = skey,
                ["pskeys"] = JsonSerializer.SerializeToNode(cookies),
            };
            return result;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }
}
