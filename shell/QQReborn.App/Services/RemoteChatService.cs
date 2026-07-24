using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Graphics.Imaging;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>QR code for real-account login, pushed by QQReborn.RealServer.</summary>
    public class QrCodeInfo
    {
        public string Url { get; set; }
        public string ImageBase64 { get; set; }
    }

    public class VoicePlayableResult
    {
        public byte[] Bytes { get; set; } = new byte[0];
        public string Format { get; set; } = "";
        public int Duration { get; set; }
    }

    /// <summary>Real-account login progress, pushed by QQReborn.RealServer.</summary>
    public class LoginStatusInfo
    {
        public string State { get; set; }
        public long Uin { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Pushed when NapCat reports a friend/group message recall.</summary>
    public class MessageRecalledInfo
    {
        public string ConversationId { get; set; }
        public string MessageId { get; set; }
        public long NapCatMessageId { get; set; }
        public long OperatorUin { get; set; }
        public long SenderUin { get; set; }
        public string SenderName { get; set; }
        public string Preview { get; set; }
    }

    /// <summary>
    /// IChatService backed by a WebSocket gateway (QQReborn.RealServer NapCat local gateway
    /// or FakeServer). Wire protocol on /ws. LoginStatusChanged is used after configureAccount
    /// binds the NapCat-logged-in account.
    /// </summary>
    public class RemoteChatService : IChatService
    {
        // Desktop debugging: localhost is loopback-exempted automatically by VS.
        // For the phone, set the PC's LAN IP (e.g. "192.168.1.10") in the
        // LocalSettings key below so the device can reach the server without a recompile.
        private const string ServerHostSettingKey = "qqr.settings.serverHost";
        private const string ServerPortSettingKey = "qqr.settings.serverPort";
        private const string AccessPasswordSettingKey = "qqr.settings.accessPassword";
        private const int DefaultServerPort = 8765;
        private const string DefaultServerHost = "localhost";

        /// <summary>
        /// Builds "ws://{host}:{port}/ws" from LocalSettings.
        /// Host: localhost / LAN IP / SakuraFrp access host.
        /// Port: 8765 at home, or the Frp remote port when outdoors.
        /// </summary>
        private static string BuildServerUrl()
        {
            var host = DefaultServerHost;
            var port = DefaultServerPort;
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values[ServerHostSettingKey] as string;
                if (!string.IsNullOrWhiteSpace(raw)) host = raw.Trim();
                var pr = Windows.Storage.ApplicationData.Current.LocalSettings.Values[ServerPortSettingKey];
                if (pr is int pi && pi > 0 && pi < 65536) port = pi;
                else if (pr is string ps && int.TryParse(ps, out var pp) && pp > 0 && pp < 65536) port = pp;
            }
            catch
            {
                // LocalSettings unavailable (e.g. unit test host) -> keep default.
            }
            return "ws://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/ws";
        }

        private static string ReadAccessPassword()
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[AccessPasswordSettingKey] as string ?? "";
            }
            catch { return ""; }
        }

        private MessageWebSocket _ws;
        private DataWriter _writer;
        private Windows.UI.Core.CoreDispatcher _dispatcher;
        private bool _connected;
        // Set right after a successful ConnectAsync and read by the "connection died"
        // handler to decide whether a reconnect loop should be started -- we only want
        // to auto-retry a connection that was up at least once, not the very first
        // connect attempt (that failure is still surfaced to the caller as-is).
        private bool _everConnected;
        // Guards against two reconnect loops running concurrently (e.g. OnClosed firing
        // while the OnMessage error path is also declaring the connection dead). Only
        // ever flipped via Interlocked, so this is safe to touch from any thread.
        private int _reconnecting;
        private readonly SemaphoreSlim _connLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        // Carry the data as a STRING across threads — Windows.Data.Json objects have
        // thread affinity and throw RPC_E_WRONG_THREAD if created on the WS receive
        // thread and read on the UI thread. Re-parse on the consumer's thread.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<MessageRecalledInfo> MessageRecalled;
        public event EventHandler<TypingState> TypingChanged;
        public event EventHandler<QrCodeInfo> QrCodeReceived;
        public event EventHandler<LoginStatusInfo> LoginStatusChanged;
        public event EventHandler SpaceFeedUpdated;

        /// <summary>Raised on the UI thread after the auto-reconnect loop re-establishes
        /// a connection that had previously dropped. Consumers (e.g. MainViewModel) use
        /// this to re-sync state that may have been missed while disconnected.</summary>
        public event EventHandler Reconnected;

        private async Task EnsureConnectedAsync()
        {
            // Captured on the UI thread (first call comes from the page) so we can
            // marshal socket-thread callbacks back to the UI thread. Must happen before
            // the _connected short-circuit below, otherwise a first call from a
            // background thread (e.g. the reconnect loop) would permanently skip the
            // capture and leave _dispatcher null forever.
            _dispatcher = _dispatcher ?? Windows.UI.Core.CoreWindow.GetForCurrentThread()?.Dispatcher;
            if (_connected) return;

            await _connLock.WaitAsync();
            try
            {
                if (_connected) return;
                // Dispose any stale socket/writer from a previous (dropped) connection so we
                // don't leak native handles or stack duplicate event subscriptions.
                CleanupSocket();
                _ws = new MessageWebSocket();
                _ws.Control.MessageType = SocketMessageType.Utf8;
                _ws.MessageReceived += OnMessage;
                _ws.Closed += OnClosed;
                var connectTask = _ws.ConnectAsync(new Uri(BuildServerUrl())).AsTask();
                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(10)));
                if (completed != connectTask)
                    throw new TimeoutException("连接 RealServer 超时");
                await connectTask;
                _writer = new DataWriter(_ws.OutputStream);
                _connected = true;
                await AuthenticateAsync();
                _everConnected = true;
            }
            catch
            {
                CleanupSocket();
                throw;
            }
            finally { _connLock.Release(); }
        }

        private async Task AuthenticateAsync()
        {
            var id = Guid.NewGuid().ToString("N");
            var req = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(id),
                ["type"] = JsonValue.CreateStringValue("auth"),
                ["password"] = JsonValue.CreateStringValue(ReadAccessPassword())
            };
            var tcs = new TaskCompletionSource<string>();
            _pending[id] = tcs;
            await _writeLock.WaitAsync();
            try
            {
                if (_writer == null) throw new InvalidOperationException("连接已关闭");
                _writer.WriteString(req.Stringify());
                await _writer.StoreAsync();
            }
            catch
            {
                _pending.TryRemove(id, out _);
                HandleConnectionDead();
                throw;
            }
            finally { _writeLock.Release(); }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (cts.Token.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException("网关鉴权超时")); }))
            {
                await tcs.Task;
            }
        }

        /// <summary>Detach handlers and dispose the current socket/writer. Caller must hold _connLock
        /// (or be in a state where no other connect is in flight).</summary>
        private void CleanupSocket()
        {
            var ws = _ws;
            var writer = _writer;
            _ws = null;
            _writer = null;
            if (ws != null)
            {
                ws.MessageReceived -= OnMessage;
                ws.Closed -= OnClosed;
            }
            try { writer?.DetachStream(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { ws?.Dispose(); } catch { }
        }

        public async Task ForceReconnectAsync()
        {
            await _connLock.WaitAsync();
            try
            {
                CleanupSocket();
                _connected = false;
            }
            finally { _connLock.Release(); }

            foreach (var kv in _pending) kv.Value.TrySetException(new Exception("connection closed"));
            _pending.Clear();

            try
            {
                await EnsureConnectedAsync();
                RunOnUi(() => Reconnected?.Invoke(this, EventArgs.Empty));
            }
            catch
            {
                TryStartReconnectLoop();
            }
        }

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            HandleConnectionDead();
        }

        /// <summary>
        /// Single place that tears down connection state once we know the socket is no
        /// longer usable, called from three sites: OnClosed (graceful close-frame),
        /// OnMessage's GetDataReader catch (transport-level failure, e.g. TCP reset),
        /// and RequestAsync's write-failure catch. May run on the socket thread or a
        /// caller's thread (RequestAsync runs on whatever thread awaited it) -- all
        /// field writes here are plain assignments/ConcurrentDictionary ops, matching
        /// the rest of this class's threading style, since .NET guarantees these
        /// individual writes don't tear even without a lock; worst case under a race is
        /// a redundant reconnect-loop kick, which TryStartReconnectLoop already guards
        /// against via Interlocked.
        /// </summary>
        private void HandleConnectionDead()
        {
            var wasConnected = _connected;
            _connected = false;
            // Drop the dead writer/socket so the next request reconnects cleanly rather than
            // writing into a disposed/half-open stream.
            _writer = null;
            foreach (var kv in _pending) kv.Value.TrySetException(new Exception("connection closed"));
            _pending.Clear();

            // Only auto-retry a connection that was actually up before (not the very
            // first connect attempt, whose failure is surfaced directly to the caller
            // via the awaited EnsureConnectedAsync/ConnectAsync exception).
            if (wasConnected && _everConnected) TryStartReconnectLoop();
        }

        /// <summary>
        /// Starts the background reconnect loop unless one is already running. Safe to
        /// call from any thread/multiple call sites concurrently -- Interlocked.CompareExchange
        /// ensures only one loop is ever in flight at a time.
        /// </summary>
        private void TryStartReconnectLoop()
        {
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            var ignore = ReconnectLoopAsync();
        }

        /// <summary>
        /// Retries EnsureConnectedAsync with exponential backoff (2s/4s/8s/16s, capped at
        /// 30s) until it succeeds, then fires Reconnected on the UI thread. Runs entirely
        /// on background threads via Task.Delay (never a DispatcherTimer, which requires
        /// UI-thread creation); EnsureConnectedAsync internally serializes on _connLock,
        /// so this is safe to race against a concurrent RequestAsync-triggered connect.
        /// </summary>
        private async Task ReconnectLoopAsync()
        {
            try
            {
                var delay = TimeSpan.FromSeconds(2);
                var maxDelay = TimeSpan.FromSeconds(30);
                while (true)
                {
                    await Task.Delay(delay);
                    try
                    {
                        await EnsureConnectedAsync();
                        RunOnUi(() => Reconnected?.Invoke(this, EventArgs.Empty));
                        return;
                    }
                    catch
                    {
                        // Still down -- back off and try again.
                        var nextTicks = delay.Ticks * 2;
                        delay = nextTicks < maxDelay.Ticks ? TimeSpan.FromTicks(nextTicks) : maxDelay;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        }

        private void OnMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            string text;
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    text = reader.ReadString(reader.UnconsumedBufferLength);
                }
            }
            catch
            {
                // GetDataReader/ReadString failing means the transport itself is broken
                // (e.g. TCP reset) -- unlike a bad JSON payload below, this means the
                // connection is dead and must be torn down/reconnected.
                HandleConnectionDead();
                return;
            }

            if (!JsonObject.TryParse(text, out var frame)) return;
            var type = frame.GetNamedString("type", "");

            // Convert WinRT-JSON to plain CLR (string / POCO) HERE on the socket thread,
            // then marshal the plain values to the UI thread.
            if (type == "result")
            {
                var id = frame.GetNamedString("id", "");
                var error = frame.ContainsKey("error") ? Str(frame, "error") : null;
                if (!string.IsNullOrEmpty(error))
                {
                    RunOnUi(() => { if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new Exception(error)); });
                }
                else
                {
                    var dataStr = frame.GetNamedValue("data").Stringify();
                    RunOnUi(() => { if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(dataStr); });
                }
            }
            else if (type == "messageReceived")
            {
                var msg = ParseMessage(frame.GetNamedObject("data"));
                RunOnUi(() => MessageReceived?.Invoke(this, msg));
            }
            else if (type == "messageRecalled")
            {
                var data = frame.GetNamedObject("data");
                var info = new MessageRecalledInfo
                {
                    ConversationId = Str(data, "conversationId"),
                    MessageId = Str(data, "messageId"),
                    NapCatMessageId = (long)data.GetNamedNumber("napcatMessageId", 0),
                    OperatorUin = (long)data.GetNamedNumber("operatorUin", 0),
                    SenderUin = (long)data.GetNamedNumber("senderUin", 0),
                    SenderName = Str(data, "senderName"),
                    Preview = Str(data, "preview"),
                };
                RunOnUi(() => MessageRecalled?.Invoke(this, info));
            }
            else if (type == "typing")
            {
                var data = frame.GetNamedObject("data");
                var st = new TypingState
                {
                    ConversationId = Str(data, "conversationId"),
                    IsTyping = data.GetNamedBoolean("isTyping", false),
                };
                RunOnUi(() => TypingChanged?.Invoke(this, st));
            }
            else if (type == "qrCode")
            {
                var data = frame.GetNamedObject("data");
                var info = new QrCodeInfo { Url = Str(data, "url"), ImageBase64 = Str(data, "imageBase64") };
                RunOnUi(() => QrCodeReceived?.Invoke(this, info));
            }
            else if (type == "loginStatus")
            {
                var data = frame.GetNamedObject("data");
                var info = new LoginStatusInfo
                {
                    State = Str(data, "state"),
                    Uin = (long)data.GetNamedNumber("uin", 0),
                    Message = Str(data, "message"),
                };
                RunOnUi(() => LoginStatusChanged?.Invoke(this, info));
            }
            else if (type == "spaceFeedUpdated")
            {
                var data = frame.GetNamedObject("data");
                // Parse hasMore from push data so the VM can show/hide "load more" button.
                if (data != null && data.ContainsKey("hasMore"))
                    SpaceFeedHasMore = data.GetNamedBoolean("hasMore", true);
                RunOnUi(() => SpaceFeedUpdated?.Invoke(this, EventArgs.Empty));
            }
        }

        private void RunOnUi(Action action)
        {
            var d = _dispatcher;
            if (d != null && !d.HasThreadAccess)
            {
                var ignore = d.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => action());
            }
            else
            {
                action();
            }
        }

        private async Task<string> RequestAsync(string type, Action<JsonObject> fill, int timeoutSeconds = 10)
        {
            await EnsureConnectedAsync();

            var id = Guid.NewGuid().ToString("N");
            var req = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(id),
                ["type"] = JsonValue.CreateStringValue(type),
            };
            fill?.Invoke(req);

            var tcs = new TaskCompletionSource<string>();
            _pending[id] = tcs;

            await _writeLock.WaitAsync();
            try
            {
                var writer = _writer;
                if (writer == null) throw new InvalidOperationException("connection closed");
                writer.WriteString(req.Stringify());
                await writer.StoreAsync();
            }
            catch
            {
                // Write failed -- the connection is dead (e.g. writing into a half-open
                // stream after an unnoticed drop). Tear down connection state the same
                // way OnClosed/OnMessage do so _connected doesn't stay stuck true, then
                // drop this pending entry so it doesn't linger awaiting a reply that will
                // never come, and surface the failure to the caller.
                HandleConnectionDead();
                _pending.TryRemove(id, out _);
                throw;
            }
            finally { _writeLock.Release(); }

            // Image/highway uploads to Tencent routinely exceed the default 10s used for
            // lightweight get*/send-text RPCs; callers of media sends pass a longer budget.
            var seconds = timeoutSeconds < 1 ? 10 : timeoutSeconds;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            using (cts.Token.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException()); }))
            {
                return await tcs.Task;
            }
        }

        public async Task<SelfProfile> GetSelfAsync()
        {
            var data = JsonObject.Parse(await RequestAsync("getSelf", null));
            return new SelfProfile
            {
                Uin = (long)data.GetNamedNumber("uin", 0),
                Nickname = Str(data, "nickname"),
                AvatarPath = Str(data, "avatarPath"),
                Signature = Str(data, "signature"),
                Level = (int)data.GetNamedNumber("level", 0),
            };
        }

        public async Task<IReadOnlyList<ChatConversation>> GetConversationsAsync()
        {
            var arr = JsonArray.Parse(await RequestAsync("getConversations", null));
            var list = new List<ChatConversation>();
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new ChatConversation
                {
                    Id = Str(o, "id"),
                    Kind = Str(o, "kind") == "Group" ? ConversationKind.Group : ConversationKind.Friend,
                    Title = Str(o, "title"),
                    AvatarPath = Str(o, "avatarPath"),
                    Preview = Str(o, "preview"),
                    LastTime = ParseTime(Str(o, "lastTime")),
                    Unread = (int)o.GetNamedNumber("unread", 0),
                    Announcement = Str(o, "announcement"),
                    IsPinned = o.GetNamedBoolean("isPinned", false),
                    IsMuted = o.GetNamedBoolean("isMuted", false),
                });
            }
            return list;
        }

        /// <summary>Set pin/mute flags on the backend. Omitted (null) flags are left alone
        /// server-side; on success we don't mutate a local model here -- the caller owns that.</summary>
        public async Task SetConversationFlagsAsync(string conversationId, bool? isPinned, bool? isMuted)
        {
            // Persist mute gate first so Windows Toast can suppress notifications even if
            // the server round-trip is slow or the conversation cache is stale.
            if (isMuted.HasValue && !string.IsNullOrEmpty(conversationId))
            {
                NotificationMuteGate.SetConversationMuted(conversationId, isMuted.Value);
                if (isMuted.Value) UnreadBadgeStore.Clear(conversationId);
            }

            await RequestAsync("setConversationFlags", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId ?? "");
                if (isPinned.HasValue) r["isPinned"] = JsonValue.CreateBooleanValue(isPinned.Value);
                if (isMuted.HasValue) r["isMuted"] = JsonValue.CreateBooleanValue(isMuted.Value);
            });
        }

        /// <summary>Clear server-side unread while the user is viewing this conversation
        /// (so live messages don't leave a badge after they go back to the list).</summary>
        public async Task MarkConversationReadAsync(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try
            {
                await RequestAsync("markConversationRead",
                    r => r["conversationId"] = JsonValue.CreateStringValue(conversationId));
            }
            catch
            {
                // Best-effort.
            }
        }

        public async Task<IReadOnlyList<Contact>> GetContactsAsync()
        {
            var arr = JsonArray.Parse(await RequestAsync("getContacts", null));
            var list = new List<Contact>();
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new Contact
                {
                    Uin = (long)o.GetNamedNumber("uin", 0),
                    Name = Str(o, "name"),
                    AvatarPath = Str(o, "avatarPath"),
                    Signature = Str(o, "signature"),
                    Online = o.GetNamedBoolean("online", false),
                });
            }
            return list;
        }

        public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, bool localOnly = false)
        {
            // Opening a chat may trigger a one-shot cloud pull (30s). Search uses localOnly
            // and only hits the session cache (fast, no sign/history storm).
            var arr = JsonArray.Parse(await RequestAsync("getMessages", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                if (localOnly) r["localOnly"] = JsonValue.CreateBooleanValue(true);
            }, timeoutSeconds: localOnly ? 10 : 30));
            var list = new List<ChatMessage>();
            foreach (var n in arr) list.Add(ParseMessage(n.GetObject()));
            return list;
        }

        public Task<ChatMessage> SendTextAsync(string conversationId, string text, string mentionsJson = null)
            => SendAsync(conversationId, "Text", text, null, null, 0, r =>
            {
                if (!string.IsNullOrEmpty(mentionsJson)) r["mentions"] = Windows.Data.Json.JsonValue.CreateStringValue(mentionsJson);
            });

        /// <summary>
        /// RealServer-only: sends a text message with a real reply-quote embedded in the
        /// protocol chain (not just a client-side cosmetic stamp -- see ConversationViewModel,
        /// which uses this instead of SendTextAsync when replying against a RemoteChatService).
        /// replyToMessageId is our own wire id ("{conversationId}:{sequence}"), which the
        /// server maps back to the real BotMessage it needs to build a proper ReplyEntity.
        /// </summary>
        public async Task<ChatMessage> SendTextWithReplyAsync(string conversationId, string text, string replyToMessageId, string mentionsJson = null)
        {
            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["contentType"] = JsonValue.CreateStringValue("Text");
                r["text"] = JsonValue.CreateStringValue(text);
                r["voiceSeconds"] = JsonValue.CreateNumberValue(0);
                if (!string.IsNullOrEmpty(replyToMessageId)) r["replyToId"] = JsonValue.CreateStringValue(replyToMessageId);
                if (!string.IsNullOrEmpty(mentionsJson)) r["mentions"] = JsonValue.CreateStringValue(mentionsJson);
            }));
            return ParseMessage(data);
        }

        public async Task<ChatMessage> SendImageAsync(string conversationId, string imagePath)
            => await SendMixedAsync(conversationId, null, new[] { imagePath }, null, null);

        public async Task<ChatMessage> SendMixedAsync(string conversationId, string text, IReadOnlyList<string> imagePaths, string replyToMessageId = null, string mentionsJson = null)
        {
            if (imagePaths == null || imagePaths.Count == 0)
                throw new ArgumentException("imagePaths required for mixed send", nameof(imagePaths));

            var localPaths = new List<string>();
            var b64List = new List<string>();
            foreach (var path in imagePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var b64 = await EncodeImageForSendAsync(path);
                if (string.IsNullOrEmpty(b64)) continue;
                b64List.Add(b64);
                localPaths.Add(path);
            }
            if (b64List.Count == 0) throw new InvalidOperationException("empty-image");

            var hasText = !string.IsNullOrWhiteSpace(text);
            var contentType = hasText ? "Mixed" : (b64List.Count == 1 ? "Image" : "Mixed");

            var imagesJson = new JsonArray();
            foreach (var b in b64List) imagesJson.Add(JsonValue.CreateStringValue(b));

            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["contentType"] = JsonValue.CreateStringValue(contentType);
                if (hasText) r["text"] = JsonValue.CreateStringValue(text.Trim());
                r["imageBase64"] = JsonValue.CreateStringValue(b64List[0]);
                r["imagesBase64"] = imagesJson;
                r["voiceSeconds"] = JsonValue.CreateNumberValue(0);
                if (!string.IsNullOrEmpty(replyToMessageId)) r["replyToId"] = JsonValue.CreateStringValue(replyToMessageId);
                if (!string.IsNullOrEmpty(mentionsJson)) r["mentions"] = JsonValue.CreateStringValue(mentionsJson);
            }, timeoutSeconds: 90));

            var msg = ParseMessage(data);
            PatchLocalImagePaths(msg, localPaths);
            return msg;
        }

        /// <summary>When the server reply has no CDN URL yet, bind local copies so the bubble
        /// still shows the image(s) we just sent (especially important for 图文混排 elements).</summary>
        private static void PatchLocalImagePaths(ChatMessage msg, IReadOnlyList<string> localPaths)
        {
            if (msg == null || localPaths == null || localPaths.Count == 0) return;
            if (string.IsNullOrEmpty(msg.ImagePath))
                msg.ImagePath = localPaths[0];

            int imgIndex = 0;
            if (msg.Elements != null)
            {
                foreach (var el in msg.Elements)
                {
                    if (el == null || !el.IsImage) continue;
                    if (string.IsNullOrEmpty(el.Url) && imgIndex < localPaths.Count)
                        el.Url = localPaths[imgIndex];
                    imgIndex++;
                }
            }

            // Server sometimes returns pure Image with empty elements; synthesize mixed parts
            // so caption + images render in one bubble when we have both.
            if (!string.IsNullOrEmpty(msg.Text) && msg.Text != "[图片]" && msg.Text != "[表情]"
                && (msg.Elements == null || msg.Elements.Count == 0)
                && !string.IsNullOrEmpty(msg.ImagePath))
            {
                msg.Elements.Add(new MessageElement { Type = "Text", Text = msg.Text });
                foreach (var p in localPaths)
                    msg.Elements.Add(new MessageElement { Type = "Image", Url = p });
                msg.ContentType = MessageContentType.Text;
            }
        }

        public async Task<ChatMessage> SendStickerAsync(string conversationId, string stickerPath)
        {
            // Stickers in the mock are ms-appx assets; for the real bridge re-use the image
            // pipeline (subType handled server-side via contentType "Sticker"). If the asset
            // can't be opened we fall through to a text placeholder rather than crashing.
            try
            {
                var base64 = await EncodeImageForSendAsync(stickerPath);
                var data = JsonObject.Parse(await RequestAsync("send", r =>
                {
                    r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                    r["contentType"] = JsonValue.CreateStringValue("Sticker");
                    r["imageBase64"] = JsonValue.CreateStringValue(base64);
                    r["voiceSeconds"] = JsonValue.CreateNumberValue(0);
                }, timeoutSeconds: 60));
                var msg = ParseMessage(data);
                if (string.IsNullOrEmpty(msg.ImagePath) && !string.IsNullOrEmpty(stickerPath))
                    msg.ImagePath = stickerPath;
                return msg;
            }
            catch
            {
                return await SendAsync(conversationId, "Text", "[表情]", null, null, 0);
            }
        }

        public async Task<ChatMessage> SendVoiceAsync(string conversationId, string audioPath, int seconds)
        {
            // Ship raw audio bytes as base64 (same idea as images). QQ prefers silk/amr;
            // m4a from the mic may fail server-side upload — error surfaces to the UI.
            var base64 = await EncodeFileBase64Async(audioPath);
            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["contentType"] = JsonValue.CreateStringValue("Voice");
                r["audioBase64"] = JsonValue.CreateStringValue(base64);
                r["voiceSeconds"] = JsonValue.CreateNumberValue(seconds);
            }, timeoutSeconds: 60));
            var msg = ParseMessage(data);
            if (string.IsNullOrEmpty(msg.AudioPath) && !string.IsNullOrEmpty(audioPath))
                msg.AudioPath = audioPath;
            if (msg.VoiceSeconds <= 0) msg.VoiceSeconds = seconds;
            return msg;
        }

        public Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb)
            => SendAsync(conversationId, "Location", placeName, null, null, 0, r =>
            {
                if (placeName != null) r["placeName"] = JsonValue.CreateStringValue(placeName);
                if (address != null) r["address"] = JsonValue.CreateStringValue(address);
                if (thumb != null) r["thumb"] = JsonValue.CreateStringValue(thumb);
            });

        public async Task<ChatMessage> SendFileAsync(string conversationId, byte[] fileBytes, string fileName)
        {
            var b64 = Convert.ToBase64String(fileBytes);
            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["contentType"] = JsonValue.CreateStringValue("File");
                r["fileBase64"] = JsonValue.CreateStringValue(b64);
                r["fileName"] = JsonValue.CreateStringValue(fileName);
            }));
            return ParseMessage(data);
        }

        public async Task<string> GetFileDownloadUrlAsync(string conversationId, string fileId)
        {
            var data = JsonObject.Parse(await RequestAsync("getFileDownloadUrl", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["fileId"] = JsonValue.CreateStringValue(fileId);
            }));
            // If server returned an error, throw so ConversationPage can show it
            if (data.ContainsKey("error"))
                throw new Exception(data.GetNamedString("error"));
            return Str(data, "url");
        }

        public async Task<ChatMessage> ForwardMessageAsync(string targetConversationId, string messageId)
        {
            var data = JsonObject.Parse(await RequestAsync("forward", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(targetConversationId);
                r["messageId"] = JsonValue.CreateStringValue(messageId);
            }));
            return ParseMessage(data);
        }

        public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string conversationId)
        {
            var arr = JsonArray.Parse(await RequestAsync("getGroupMembers",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<GroupMember>();
            foreach (var n in arr)
            {
                var o = n.GetObject();
                list.Add(new GroupMember
                {
                    Uin = (long)o.GetNamedNumber("uin", 0),
                    Name = Str(o, "name"),
                    AvatarPath = Str(o, "avatarPath"),
                    Role = Str(o, "role"),
                });
            }
            return list;
        }

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
            // Honor whatever the backend actually did -- the real server can't accept a
            // friend request at all (no such API in LagrangeV2) and says so honestly via
            // handled:false; don't paper over that by always flipping this to true.
            request.Handled = data.GetNamedBoolean("handled", false);
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
        public async Task<EarlierMessagesResult> GetEarlierMessagesAsync(string conversationId, string beforeMessageId, int count)
        {
            var data = JsonObject.Parse(await RequestAsync("getEarlierMessages", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                if (!string.IsNullOrEmpty(beforeMessageId))
                    r["beforeId"] = JsonValue.CreateStringValue(beforeMessageId);
                r["count"] = JsonValue.CreateNumberValue(count);
            }, timeoutSeconds: 30));
            var list = new List<ChatMessage>();
            foreach (var n in data.GetNamedArray("messages", new JsonArray())) list.Add(ParseMessage(n.GetObject()));
            return new EarlierMessagesResult
            {
                Messages = list,
                HasMore = data.GetNamedBoolean("hasMore", false),
            };
        }

        /// <summary>Recalls (withdraws) a previously sent message. Returns whatever the server
        /// reports via data.recalled -- e.g. false past the recall time window -- rather than
        /// assuming success; callers should inspect the return value before mutating UI state.</summary>
        public async Task<bool> RecallMessageAsync(string conversationId, string messageId)
        {
            var data = JsonObject.Parse(await RequestAsync("recallMessage", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["messageId"] = JsonValue.CreateStringValue(messageId);
            }));
            return data.GetNamedBoolean("recalled", false);
        }

        /// <summary>Leaves a group conversation. Returns data.left as reported by the server
        /// (e.g. false if the backend can't perform the action, same honesty convention as
        /// AcceptFriendRequestAsync's handled flag).</summary>
        public async Task<bool> QuitGroupAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("quitGroup",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            return data.GetNamedBoolean("left", false);
        }

        /// <summary>Sends a "poke"/nudge to the given target within a conversation. Returns
        /// data.sent as reported by the server (same honesty convention as AcceptFriendRequestAsync's
        /// handled flag / QuitGroupAsync's left flag).</summary>
        public async Task<bool> SendNudgeAsync(string conversationId, long targetUin)
        {
            var data = JsonObject.Parse(await RequestAsync("nudge", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
            }));
            return data.GetNamedBoolean("sent", false);
        }

        public async Task<bool> GroupRenameAsync(string conversationId, string newName)
        {
            var data = JsonObject.Parse(await RequestAsync("groupRename", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["name"] = JsonValue.CreateStringValue(newName);
            }));
            return data.GetNamedBoolean("renamed", false);
        }

        public async Task<bool> GroupMemberRenameAsync(string conversationId, long targetUin, string newName)
        {
            var data = JsonObject.Parse(await RequestAsync("groupMemberRename", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["name"] = JsonValue.CreateStringValue(newName);
            }));
            return data.GetNamedBoolean("renamed", false);
        }

        public async Task<bool> GroupSetSpecialTitleAsync(string conversationId, long targetUin, string title)
        {
            var data = JsonObject.Parse(await RequestAsync("groupSetSpecialTitle", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
                r["title"] = JsonValue.CreateStringValue(title);
            }));
            return data.GetNamedBoolean("set", false);
        }

        /// <summary>Uploads a new avatar image (base64-encoded) for the logged-in user. Returns
        /// data.ok as reported by the server.</summary>
        public async Task<bool> SetAvatarAsync(string imageBase64)
        {
            var data = JsonObject.Parse(await RequestAsync("setAvatar",
                r => r["imageBase64"] = JsonValue.CreateStringValue(imageBase64)));
            return data.GetNamedBoolean("ok", false);
        }

        /// <summary>Resolves a downloadable URL for a message's media (e.g. video) payload.
        /// data.url may come back as JSON null, hence the null-safe Str() helper rather than
        /// GetNamedString.</summary>
        public async Task<string> GetMediaUrlAsync(string messageId)
        {
            var data = JsonObject.Parse(await RequestAsync("getMediaUrl",
                r => r["messageId"] = JsonValue.CreateStringValue(messageId)));
            return Str(data, "url");
        }

        public async Task<VoicePlayableResult> GetVoicePlayableAsync(string messageId)
        {
            var result = new VoicePlayableResult();
            var raw = await RequestAsync("getVoicePlayable",
                r => r["messageId"] = JsonValue.CreateStringValue(messageId));
            if (string.IsNullOrEmpty(raw) || raw == "null") return result;
            var data = JsonObject.Parse(raw);
            if (data == null) return result;
            var b64 = Str(data, "audioBase64");
            result.Format = Str(data, "format");
            result.Duration = (int)data.GetNamedNumber("duration", 0);
            if (!string.IsNullOrEmpty(b64))
            {
                result.Bytes = Convert.FromBase64String(b64);
            }
            return result;
        }

        public async Task<JsonArray> GetGroupNotificationsAsync()
        {
            var raw = await RequestAsync("getGroupNotifications", null);
            if (string.IsNullOrEmpty(raw) || raw == "null") return new JsonArray();
            var data = JsonObject.Parse(raw);
            return data?.GetNamedArray("notifications", new JsonArray()) ?? new JsonArray();
        }

        public async Task<bool> HandleGroupNotificationAsync(long groupUin, ulong sequence, string notifType, string operate, string message = "", bool isFiltered = false)
        {
            var raw = await RequestAsync("handleGroupNotification", r =>
            {
                r["groupUin"] = JsonValue.CreateNumberValue(groupUin);
                r["sequence"] = JsonValue.CreateNumberValue(sequence);
                r["notifType"] = JsonValue.CreateStringValue(notifType);
                r["operate"] = JsonValue.CreateStringValue(operate);
                r["message"] = JsonValue.CreateStringValue(message ?? "");
                r["isFiltered"] = JsonValue.CreateBooleanValue(isFiltered);
            });
            if (string.IsNullOrEmpty(raw) || raw == "null") return false;
            var data = JsonObject.Parse(raw);
            return data?.GetNamedBoolean("ok", false) == true;
        }

        /// <summary>Group message reaction (emoji). isAdd=false removes.</summary>
        public async Task<bool> SetGroupReactionAsync(string conversationId, string messageId, string code, bool isAdd)
        {
            var raw = await RequestAsync("setGroupReaction", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId ?? "");
                r["messageId"] = JsonValue.CreateStringValue(messageId ?? "");
                r["code"] = JsonValue.CreateStringValue(code ?? "");
                r["isAdd"] = JsonValue.CreateBooleanValue(isAdd);
            });
            if (string.IsNullOrEmpty(raw) || raw == "null") return false;
            var data = JsonObject.Parse(raw);
            return data?.GetNamedBoolean("ok", false) == true;
        }

        /// <summary>Space / 动态 feed from webhook-ingested posts on RealServer.</summary>
        public async Task<IReadOnlyList<Moment>> GetSpaceFeedAsync()
        {
            var list = new List<Moment>();
            try
            {
                var raw = await RequestAsync("getMoments", null);
                if (string.IsNullOrEmpty(raw) || raw == "null") return list;
                var data = JsonObject.Parse(raw);
                if (data == null) return list;
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
                            m.Comments.Add(new MomentComment
                            {
                                Author = Str(c, "author") ?? Str(c, "authorName"),
                                Text = Str(c, "text") ?? Str(c, "content"),
                            });
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

        /// <summary>
        /// Kicks off real-account login on the backend (RealServer only). The fake demo
        /// server has no "configureAccount" handler and echoes data:null -- note that
        /// JsonObject.Parse("null") THROWS in Windows.Data.Json (top level must be an
        /// object), so that case is checked explicitly and reported as a graceful false.
        /// Login itself proceeds in the background; watch QrCodeReceived/LoginStatusChanged
        /// for progress.
        /// </summary>
        public async Task<bool> ConfigureAccountAsync(string signUrl, string signToken, string signUin)
        {
            var raw = await RequestAsync("configureAccount", r =>
            {
                r["signUrl"] = JsonValue.CreateStringValue(signUrl ?? "");
                r["signToken"] = JsonValue.CreateStringValue(signToken ?? "");
                r["signUin"] = JsonValue.CreateStringValue(signUin ?? "");
            });
            JsonObject data;
            if (string.IsNullOrEmpty(raw) || raw == "null" || !JsonObject.TryParse(raw, out data))
                return false;
            return data.GetNamedBoolean("accepted", false);
        }

        // ---- channel / guild ----

        /// <summary>Send a channel.* command through the shared WebSocket transport.</summary>
        public async Task<string> SendChannelRequestAsync(string type, Dictionary<string, object> args)
        {
            return await RequestAsync(type, r =>
            {
                if (args == null) return;
                foreach (var kv in args)
                {
                    if (kv.Value is string s)
                        r[kv.Key] = JsonValue.CreateStringValue(s);
                    else if (kv.Value is long l)
                        r[kv.Key] = JsonValue.CreateNumberValue(l);
                    else if (kv.Value is int i)
                        r[kv.Key] = JsonValue.CreateNumberValue(i);
                    else if (kv.Value is bool b)
                        r[kv.Key] = JsonValue.CreateBooleanValue(b);
                }
            });
        }

        private async Task<ChatMessage> SendAsync(string convId, string contentType, string text, string imagePath, string audioPath, int seconds, Action<JsonObject> extra = null)
        {
            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(convId);
                r["contentType"] = JsonValue.CreateStringValue(contentType);
                if (text != null) r["text"] = JsonValue.CreateStringValue(text);
                if (imagePath != null) r["imagePath"] = JsonValue.CreateStringValue(imagePath);
                if (audioPath != null) r["audioPath"] = JsonValue.CreateStringValue(audioPath);
                r["voiceSeconds"] = JsonValue.CreateNumberValue(seconds);
                extra?.Invoke(r);
            }));
            return ParseMessage(data);
        }

        // ---- mapping ----

        private static ChatMessage ParseMessage(JsonObject o)
        {
            var type = Str(o, "contentType");
            var msg = new ChatMessage
            {
                Id = Str(o, "id"),
                ConversationId = Str(o, "conversationId"),
                ConversationTitle = Str(o, "conversationTitle"),
                ConversationAvatarPath = Str(o, "conversationAvatarPath"),
                SenderName = Str(o, "senderName"),
                SenderUin = (long)o.GetNamedNumber("senderUin", 0),
                SenderAvatarPath = Str(o, "senderAvatarPath"),
                Direction = Str(o, "direction") == "Outgoing" ? MessageDirection.Outgoing : MessageDirection.Incoming,
                ContentType = ParseContentType(type),
                Text = Str(o, "text"),
                ImagePath = Str(o, "imagePath"),
                AudioPath = Str(o, "audioPath"),
                VoiceSeconds = (int)o.GetNamedNumber("voiceSeconds", 0),
                PlaceName = Str(o, "placeName"),
                PlaceAddress = Str(o, "address"),
                PlaceThumb = Str(o, "thumb"),
                ReplyToSender = Str(o, "replyToSender"),
                ReplyToText = Str(o, "replyToText"),
                FileName = Str(o, "fileName"),
                FileSize = Str(o, "fileSize"),
                FileId = Str(o, "fileId"),
                Time = ParseTime(Str(o, "time")),
                State = MessageState.Sent,
            };

            if (o.ContainsKey("elements"))
            {
                var els = o.GetNamedArray("elements");
                foreach (var elVal in els)
                {
                    if (elVal.ValueType != JsonValueType.Object) continue;
                    var el = elVal.GetObject();
                    // Server historically used PascalCase keys; accept both.
                    var elType = Str(el, "Type");
                    if (string.IsNullOrEmpty(elType)) elType = Str(el, "type");
                    var elText = Str(el, "Text");
                    if (string.IsNullOrEmpty(elText)) elText = Str(el, "text");
                    var elUrl = Str(el, "Url");
                    if (string.IsNullOrEmpty(elUrl)) elUrl = Str(el, "url");
                    long elUin = 0;
                    if (el.ContainsKey("Uin")) elUin = (long)el.GetNamedNumber("Uin", 0);
                    else if (el.ContainsKey("uin")) elUin = (long)el.GetNamedNumber("uin", 0);
                    msg.Elements.Add(new MessageElement
                    {
                        Type = elType,
                        Text = elText,
                        Url = elUrl,
                        Uin = elUin
                    });
                }
            }

            // Fill empty image element URLs from the top-level imagePath (CDN or local).
            if (!string.IsNullOrEmpty(msg.ImagePath) && msg.Elements != null)
            {
                foreach (var el in msg.Elements)
                {
                    if (el != null && el.IsImage && string.IsNullOrEmpty(el.Url))
                        el.Url = msg.ImagePath;
                }
            }

            return msg;
        }

        private static MessageContentType ParseContentType(string s)
        {
            switch (s)
            {
                case "Image": return MessageContentType.Image;
                case "Sticker": return MessageContentType.Sticker;
                case "Voice": return MessageContentType.Voice;
                case "System": return MessageContentType.System;
                case "Location": return MessageContentType.Location;
                case "Video": return MessageContentType.Video;
                case "File":
                case "FileMsg": return MessageContentType.FileMsg;
                case "LinkCard": return MessageContentType.LinkCard;
                case "Mixed": return MessageContentType.Text; // rendered via UseMixedLayout / Elements
                default: return MessageContentType.Text;
            }
        }

        private static DateTimeOffset ParseTime(string s)
        {
            if (!string.IsNullOrEmpty(s) &&
                DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                return t.ToLocalTime();
            return DateTimeOffset.Now;
        }

        private static string Str(JsonObject o, string name)
        {
            if (!o.ContainsKey(name)) return null;
            var v = o.GetNamedValue(name);
            return v.ValueType == JsonValueType.String ? v.GetString() : null;
        }

        // Chat photos: longer edge capped so base64 stays under the bridge's 2MB WS frame
        // budget (raw JPEG ~0.6–0.9MB → base64 ~0.8–1.2MB). Matches ProfileView's avatar
        // encoder, just with a larger edge allowance for conversation photos.
        private const uint MaxChatImageEdge = 1280;

        /// <summary>Read a local (ms-appdata / absolute) file into base64 for WS upload.</summary>
        private static async Task<string> EncodeFileBase64Async(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path empty");
            StorageFile file;
            if (path.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(path));
            }
            else
            {
                file = await StorageFile.GetFileFromPathAsync(path);
            }
            var buf = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buf.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buf))
                reader.ReadBytes(bytes);
            if (bytes.Length > 1500 * 1024)
                throw new InvalidOperationException("audio-too-large");
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Load a local (ms-appdata / ms-appx / absolute) image, downscale so the long edge
        /// is at most <see cref="MaxChatImageEdge"/>, re-encode as JPEG @ ~0.8 quality, and
        /// return base64. Used by SendImageAsync / SendStickerAsync so the PC-side RealServer
        /// never needs to open phone-local paths.
        /// </summary>
        private static async Task<string> EncodeImageForSendAsync(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) throw new ArgumentException("imagePath empty");

            StorageFile file;
            if (imagePath.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase)
                || imagePath.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(imagePath));
            }
            else
            {
                file = await StorageFile.GetFileFromPathAsync(imagePath);
            }

            using (var inputStream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                double scale = 1.0;
                var longEdge = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                if (longEdge > MaxChatImageEdge) scale = (double)MaxChatImageEdge / longEdge;

                var targetWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                var targetHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = targetWidth,
                    ScaledHeight = targetHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                var pixels = pixelData.DetachPixelData();

                using (var outputStream = new InMemoryRandomAccessStream())
                {
                    var propertySet = new BitmapPropertySet
                    {
                        ["ImageQuality"] = new BitmapTypedValue(0.8f, Windows.Foundation.PropertyType.Single)
                    };
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, propertySet);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        targetWidth, targetHeight, decoder.DpiX, decoder.DpiY, pixels);
                    await encoder.FlushAsync();

                    var bytes = new byte[outputStream.Size];
                    outputStream.Seek(0);
                    using (var reader = new DataReader(outputStream.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)outputStream.Size);
                        reader.ReadBytes(bytes);
                    }
                    return Convert.ToBase64String(bytes);
                }
            }
        }
    }
}
