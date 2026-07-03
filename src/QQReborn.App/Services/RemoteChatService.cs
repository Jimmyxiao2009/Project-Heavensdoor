using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// IChatService backed by the local fake WebSocket server (QQReborn.FakeServer).
    /// Same contract as MockChatService, but data and auto-reply live in the server.
    /// This is the network layer rehearsal for the eventual real-protocol backend.
    /// </summary>
    public class RemoteChatService : IChatService
    {
        // Desktop debugging: localhost is loopback-exempted automatically by VS.
        // For the phone, set the PC's LAN IP (e.g. "192.168.1.10") in the
        // LocalSettings key below so the device can reach the server without a recompile.
        private const string ServerHostSettingKey = "qqr.settings.serverHost";
        private const int ServerPort = 8765;
        private const string DefaultServerHost = "localhost";

        /// <summary>
        /// Builds "ws://{host}:8765/ws" from the persisted host setting, defaulting to
        /// localhost. The host can be overridden at runtime via LocalSettings so the
        /// phone scenario works (enter the PC's LAN IP) without changing code.
        /// </summary>
        private static string BuildServerUrl()
        {
            var host = DefaultServerHost;
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values[ServerHostSettingKey] as string;
                if (!string.IsNullOrWhiteSpace(raw)) host = raw.Trim();
            }
            catch
            {
                // LocalSettings unavailable (e.g. unit test host) -> keep default.
            }
            return "ws://" + host + ":" + ServerPort.ToString(CultureInfo.InvariantCulture) + "/ws";
        }

        private MessageWebSocket _ws;
        private DataWriter _writer;
        private Windows.UI.Core.CoreDispatcher _dispatcher;
        private bool _connected;
        private readonly SemaphoreSlim _connLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        // Carry the data as a STRING across threads — Windows.Data.Json objects have
        // thread affinity and throw RPC_E_WRONG_THREAD if created on the WS receive
        // thread and read on the UI thread. Re-parse on the consumer's thread.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<TypingState> TypingChanged;

        private async Task EnsureConnectedAsync()
        {
            if (_connected) return;
            // Captured on the UI thread (first call comes from the page) so we can
            // marshal socket-thread callbacks back to the UI thread.
            _dispatcher = _dispatcher ?? Windows.UI.Core.CoreWindow.GetForCurrentThread()?.Dispatcher;

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
                await _ws.ConnectAsync(new Uri(BuildServerUrl()));
                _writer = new DataWriter(_ws.OutputStream);
                _connected = true;
            }
            finally { _connLock.Release(); }
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

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            _connected = false;
            // Drop the dead writer/socket so the next request reconnects cleanly rather than
            // writing into a disposed/half-open stream.
            _writer = null;
            foreach (var kv in _pending) kv.Value.TrySetException(new Exception("connection closed"));
            _pending.Clear();
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
            catch { return; }

            if (!JsonObject.TryParse(text, out var frame)) return;
            var type = frame.GetNamedString("type", "");

            // Convert WinRT-JSON to plain CLR (string / POCO) HERE on the socket thread,
            // then marshal the plain values to the UI thread.
            if (type == "result")
            {
                var id = frame.GetNamedString("id", "");
                var dataStr = frame.GetNamedValue("data").Stringify();
                RunOnUi(() => { if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(dataStr); });
            }
            else if (type == "messageReceived")
            {
                var msg = ParseMessage(frame.GetNamedObject("data"));
                RunOnUi(() => MessageReceived?.Invoke(this, msg));
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

        private async Task<string> RequestAsync(string type, Action<JsonObject> fill)
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
                // Drop the pending entry so it doesn't linger awaiting a reply that will
                // never come, and surface the failure to the caller.
                _pending.TryRemove(id, out _);
                throw;
            }
            finally { _writeLock.Release(); }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
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
                });
            }
            return list;
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

        public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId)
        {
            var arr = JsonArray.Parse(await RequestAsync("getMessages", r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<ChatMessage>();
            foreach (var n in arr) list.Add(ParseMessage(n.GetObject()));
            return list;
        }

        public Task<ChatMessage> SendTextAsync(string conversationId, string text)
            => SendAsync(conversationId, "Text", text, null, null, 0);

        public Task<ChatMessage> SendImageAsync(string conversationId, string imagePath)
            => SendAsync(conversationId, "Image", null, imagePath, null, 0);

        public Task<ChatMessage> SendStickerAsync(string conversationId, string stickerPath)
            => SendAsync(conversationId, "Sticker", null, stickerPath, null, 0);

        public Task<ChatMessage> SendVoiceAsync(string conversationId, string audioPath, int seconds)
            => SendAsync(conversationId, "Voice", null, null, audioPath, seconds);

        public Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb)
            => SendAsync(conversationId, "Location", placeName, null, null, 0, r =>
            {
                if (placeName != null) r["placeName"] = JsonValue.CreateStringValue(placeName);
                if (address != null) r["address"] = JsonValue.CreateStringValue(address);
                if (thumb != null) r["thumb"] = JsonValue.CreateStringValue(thumb);
            });

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
            await RequestAsync("acceptFriendRequest",
                r => r["uin"] = JsonValue.CreateNumberValue(request.Uin));
            request.Handled = true;
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
            return new ChatMessage
            {
                Id = Str(o, "id"),
                ConversationId = Str(o, "conversationId"),
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
                Time = ParseTime(Str(o, "time")),
                State = MessageState.Sent,
            };
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
    }
}
