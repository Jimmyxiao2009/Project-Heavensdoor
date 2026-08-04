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
    /// <summary>
    /// IChatService backed by RealServer (NapCat gateway) over WebSocket /ws.
    /// LoginStatusChanged fires after configureAccount binds the NapCat account.
    /// Split into partials: Api (RPC methods), Mapping (JSON -> models).
    /// </summary>
    public partial class RemoteChatService : IGatewayService
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
        /// Host: localhost / LAN IP / Frp access host (OpenFrp/Sakura/etc.).
        /// Port: 8765 at home, or the Frp remote port when outdoors (OpenFrp/Sakura/etc.).
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

            return GatewayEndpoint.BuildWsUrl(host, port);
        }

        /// <summary>Delegates to <see cref="GatewayEndpoint"/> (unit-tested pure helper).</summary>
        internal static string NormalizeServerHost(string raw, ref int port)
            => GatewayEndpoint.NormalizeServerHost(raw, ref port);

        private static bool IsLoopbackOrLanHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return true;
            host = host.Trim().Trim('[', ']');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1")
                return true;
            if (host.StartsWith("192.168.", StringComparison.Ordinal)
                || host.StartsWith("10.", StringComparison.Ordinal)
                || host.StartsWith("172.16.", StringComparison.Ordinal)
                || host.StartsWith("172.17.", StringComparison.Ordinal)
                || host.StartsWith("172.18.", StringComparison.Ordinal)
                || host.StartsWith("172.19.", StringComparison.Ordinal)
                || host.StartsWith("172.2", StringComparison.Ordinal)
                || host.StartsWith("172.30.", StringComparison.Ordinal)
                || host.StartsWith("172.31.", StringComparison.Ordinal))
                return true;
            return false;
        }

        private static int ConnectTimeoutSecondsForUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                return IsLoopbackOrLanHost(u.Host) ? 12 : 28;
            }
            catch { return 20; }
        }

        private static bool IsTransientConnectFailure(Exception ex)
        {
            if (ex == null) return false;
            if (ex is TimeoutException) return true;
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerExceptions != null && agg.InnerExceptions.Count > 0)
                ex = agg.InnerExceptions[0];
            if (ex.InnerException != null) ex = ex.InnerException;
            var hr = ex.HResult;
            var msg = ex.Message ?? "";
            if (hr == unchecked((int)0x8007274C)
                || hr == unchecked((int)0x8007274D)
                || hr == unchecked((int)0x80072743)
                || hr == unchecked((int)0x80072751)
                || hr == unchecked((int)0x80072745)
                || hr == unchecked((int)0x80072746))
                return true;
            if (msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("超时", StringComparison.Ordinal) >= 0
                || msg.IndexOf("拒绝", StringComparison.Ordinal) >= 0
                || msg.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private static string ReadAccessPassword()
        {
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[AccessPasswordSettingKey] as string ?? "";
                return raw.Trim();
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
        // The RealServer currently creates a fresh AccountSession for a fresh
        // WebSocket. Keep the last successful account binding so reconnect can
        // restore that session before consumers refresh conversations.
        private readonly object _accountGate = new object();
        private string _configuredExpectedUin = "";
        private bool _accountConfigured;
        // A RealServer session belongs to one WebSocket connection.  After a network
        // reconnect the transport may already be authenticated while its fresh backend has
        // not yet been associated with the QQ account, which used to make the first send
        // fail with "not-online".  Serialize that short rebind handshake for all callers.
        private bool _accountBoundForCurrentConnection;
        private readonly SemaphoreSlim _accountBindLock = new SemaphoreSlim(1, 1);
        // Guards against two reconnect loops running concurrently (e.g. OnClosed firing
        // while the OnMessage error path is also declaring the connection dead). Only
        // ever flipped via Interlocked, so this is safe to touch from any thread.
        private int _reconnecting;
        private readonly SemaphoreSlim _connLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        // Protects _ws/_writer/_connected swaps against socket-thread Closed callbacks.
        private readonly object _gate = new object();
        // Carry the data as a STRING across threads — Windows.Data.Json objects have
        // thread affinity and throw RPC_E_WRONG_THREAD if created on the WS receive
        // thread and read on the UI thread. Re-parse on the consumer's thread.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<MessageRecalledInfo> MessageRecalled;
        public event EventHandler<ConversationFlagsChangedInfo> ConversationFlagsChanged;

        public event EventHandler<ConversationReadInfo> ConversationRead;
        public event EventHandler<TypingState> TypingChanged;
        public event EventHandler<QrCodeInfo> QrCodeReceived;
        public event EventHandler<LoginStatusInfo> LoginStatusChanged;
        public event EventHandler SpaceFeedUpdated;

        /// <summary>Raised on the UI thread after the auto-reconnect loop re-establishes
        /// a connection that had previously dropped. Consumers (e.g. MainViewModel) use
        /// this to re-sync state that may have been missed while disconnected.</summary>
        public event EventHandler Reconnected;

        /// <summary>Raised when the NapCat backend finishes its initial background
        /// conversation/contact population after account binding.</summary>
        public event EventHandler SessionDataUpdated;

        /// <summary>
        /// Connect + auth with Frp-aware retries. Resilience contract (do not gut in refactors):
        /// multi-attempt connect, transient-only retry, wrong-password abort, CleanupSocket on
        /// every failure path, never leave _connected true on half-open sockets.
        /// See docs/RESILIENCE.md.
        /// </summary>
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

                var url = BuildServerUrl();
                var timeoutSec = ConnectTimeoutSecondsForUrl(url);
                // Frp cold path is flaky: DNS / node hop / tunnel wake. Retry a few times
                // inside the connect lock so callers only see the final outcome.
                var maxAttempts = timeoutSec >= 20 ? 3 : 2;
                Exception lastError = null;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    CleanupSocket();
                    var ws = new MessageWebSocket();
                    ws.Control.MessageType = SocketMessageType.Utf8;
                    // MessageWebSocketControl.KeepAliveInterval is not available on
                    // older UWP / W10M targets (15063). Rely on app-level reconnect.
                    ws.MessageReceived += OnMessage;
                    ws.Closed += OnClosed;
                    _ws = ws;

                    Task connectTask;
                    try
                    {
                        connectTask = ws.ConnectAsync(new Uri(url)).AsTask();
                    }
                    catch (Exception ex)
                    {
                        lastError = new Exception(FormatSocketError("无法开始连接 " + url, ex), ex);
                        CleanupSocket();
                        if (attempt >= maxAttempts || !IsTransientConnectFailure(ex)) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
                        continue;
                    }

                    var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
                    if (completed != connectTask)
                    {
                        // Abort the in-flight connect; never leave a half-open MessageWebSocket.
                        CleanupSocket();
                        // Observe the abandoned task so it doesn't surface as unhandled later.
                        var ignored = connectTask.ContinueWith(t =>
                        {
                            try { var _ = t.Exception; } catch { }
                        }, TaskContinuationOptions.OnlyOnFaulted);
                        lastError = new TimeoutException(
                            "连接网关超时（" + url + "，" + timeoutSec + "s，第" + attempt + "/" + maxAttempts
                            + "次）。请确认管家已启动、OpenFrp/Frp 隧道在线，且手机能访问该地址/端口。");
                        if (attempt >= maxAttempts) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt));
                        continue;
                    }

                    try
                    {
                        await connectTask;
                    }
                    catch (Exception ex)
                    {
                        CleanupSocket();
                        lastError = new Exception(FormatSocketError("连接网关失败（" + url + "）", ex), ex);
                        if (attempt >= maxAttempts || !IsTransientConnectFailure(ex)) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt));
                        continue;
                    }

                    _writer = new DataWriter(ws.OutputStream);
                    // Auth while still holding _connLock so no other caller races mid-handshake.
                    try
                    {
                        await AuthenticateAsync(timeoutSec >= 20 ? 18 : 10);
                    }
                    catch (Exception ex)
                    {
                        CleanupSocket();
                        _connected = false;
                        lastError = new Exception(FormatSocketError("网关鉴权失败", ex), ex);
                        // Wrong password is not transient — do not burn retries.
                        var msg = ex.Message ?? "";
                        if (msg.IndexOf("访问密码错误", StringComparison.Ordinal) >= 0
                            || msg.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0)
                            break;
                        if (attempt >= maxAttempts || !IsTransientConnectFailure(ex)) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt));
                        continue;
                    }

                    _connected = true;
                    _everConnected = true;
                    lastError = null;
                    break;
                }

                if (!_connected)
                {
                    CleanupSocket();
                    if (lastError != null) throw lastError;
                    throw new Exception("连接网关失败（" + url + "）");
                }
            }
            catch
            {
                // Ensure half-open state is never left as "_connected == true".
                if (!_connected) CleanupSocket();
                throw;
            }
            finally { _connLock.Release(); }
        }

        private async Task AuthenticateAsync(int timeoutSeconds = 10)
        {
            var id = Guid.NewGuid().ToString("N");
            var req = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(id),
                ["type"] = JsonValue.CreateStringValue("auth"),
                ["password"] = JsonValue.CreateStringValue(ReadAccessPassword())
            };
            // RunContinuationsAsynchronously avoids completing on the socket thread if a
            // waiter is already scheduled there (reduces re-entrancy on MessageWebSocket).
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await _writeLock.WaitAsync();
            try
            {
                var writer = _writer;
                if (writer == null) throw new InvalidOperationException("连接已关闭");
                writer.WriteString(req.Stringify());
                await writer.StoreAsync();
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                // Do not call HandleConnectionDead while _connLock is held by EnsureConnected
                // (deadlock). Caller tears the socket down.
                throw new Exception(FormatSocketError("发送鉴权失败", ex), ex);
            }
            finally { _writeLock.Release(); }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds < 5 ? 5 : timeoutSeconds)))
            using (cts.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t))
                    t.TrySetException(new TimeoutException("网关鉴权超时。请检查访问密码是否与管家一致。"));
            }))
            {
                await tcs.Task;
            }
        }

        /// <summary>Detach handlers and dispose the current socket/writer.
        /// Always dispose the DataWriter before the MessageWebSocket — skipping that
        /// (e.g. only nulling _writer) causes COMException 0x8000000E E_ILLEGAL_METHOD_CALL
        /// on the next connect ("A method was called at an unexpected time").</summary>
        private void CleanupSocket()
        {
            MessageWebSocket ws;
            DataWriter writer;
            lock (_gate)
            {
                ws = _ws;
                writer = _writer;
                _ws = null;
                _writer = null;
                _connected = false;
            }
            lock (_accountGate) _accountBoundForCurrentConnection = false;
            if (ws != null)
            {
                try { ws.MessageReceived -= OnMessage; } catch { }
                try { ws.Closed -= OnClosed; } catch { }
            }
            // Prefer DetachStream so disposing the writer does not close the socket stream
            // twice; if Detach fails, still Dispose both.
            try { writer?.DetachStream(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { ws?.Dispose(); } catch { }
        }

        /// <summary>User-facing message for WinRT socket / COM failures (esp. 0x8000000E).</summary>
        internal static string FormatSocketError(string prefix, Exception ex)
        {
            if (ex == null) return prefix;
            // Unwrap one level of AggregateException.
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerExceptions != null && agg.InnerExceptions.Count > 0)
                ex = agg.InnerExceptions[0];
            if (ex.InnerException != null
                && (string.IsNullOrEmpty(ex.Message) || ex.Message.IndexOf("0x", StringComparison.OrdinalIgnoreCase) >= 0))
                ex = ex.InnerException;

            var hr = ex.HResult;
            var msg = ex.Message ?? "";
            // E_ILLEGAL_METHOD_CALL — classic MessageWebSocket reuse / dispose race.
            if (hr == unchecked((int)0x8000000E)
                || msg.IndexOf("0x8000000E", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("unexpected time", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("意外的时间", StringComparison.Ordinal) >= 0)
            {
                return prefix + "：连接状态异常（0x8000000E）。请返回设置确认服务器地址/端口/访问密码后重试，或重启 App。";
            }
            // WSAECONNREFUSED / connection refused style
            if (hr == unchecked((int)0x8007274D) || hr == unchecked((int)0x8007274C)
                || msg.IndexOf("拒绝", StringComparison.Ordinal) >= 0
                || msg.IndexOf("actively refused", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return prefix + "：无法连上电脑网关。请确认管家已「启动网关」，且地址/端口正确。";
            }
            if (msg.IndexOf("访问密码", StringComparison.Ordinal) >= 0)
                return msg;
            if (!string.IsNullOrWhiteSpace(msg))
                return prefix + "：" + msg;
            return prefix + "（0x" + hr.ToString("X8") + "）";
        }

        public async Task ForceReconnectAsync()
        {
            await _connLock.WaitAsync();
            try
            {
                CleanupSocket();
            }
            finally { _connLock.Release(); }

            FailAllPending(new Exception("connection closed"));

            try
            {
                await EnsureConnectedAsync();
                await RestoreAccountAfterReconnectAsync();
                RunOnUi(() => Reconnected?.Invoke(this, EventArgs.Empty));
            }
            catch
            {
                TryStartReconnectLoop();
            }
        }

        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            // Ignore close callbacks for sockets we already replaced/disposed.
            // When _ws is null or points at a newer instance, do nothing.
            if (!object.ReferenceEquals(sender, _ws)) return;
            HandleConnectionDead();
        }

        /// <summary>
        /// Single place that tears down connection state once we know the socket is no
        /// longer usable, called from three sites: OnClosed (graceful close-frame),
        /// OnMessage's GetDataReader catch (transport-level failure, e.g. TCP reset),
        /// and RequestAsync's write-failure catch.
        /// Must fully dispose DataWriter+MessageWebSocket (see CleanupSocket) — only
        /// nulling _writer causes 0x8000000E on the next connect.
        /// </summary>
        private void HandleConnectionDead()
        {
            bool wasConnected;
            lock (_gate) { wasConnected = _connected; }
            CleanupSocket();
            FailAllPending(new Exception("connection closed"));

            // Only auto-retry a connection that was actually up before (not the very
            // first connect attempt, whose failure is surfaced directly to the caller
            // via the awaited EnsureConnectedAsync/ConnectAsync exception).
            if (wasConnected && _everConnected) TryStartReconnectLoop();
        }

        private void FailAllPending(Exception ex)
        {
            foreach (var kv in _pending) kv.Value.TrySetException(ex);
            _pending.Clear();
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
        /// Retries EnsureConnectedAsync with exponential backoff (1s/2s/4s/..., capped at
        /// 20s) until it succeeds, then fires Reconnected on the UI thread. Runs entirely
        /// on background threads via Task.Delay (never a DispatcherTimer, which requires
        /// UI-thread creation); EnsureConnectedAsync internally serializes on _connLock,
        /// so this is safe to race against a concurrent RequestAsync-triggered connect.
        /// </summary>
        private async Task ReconnectLoopAsync()
        {
            try
            {
                // Start quickly: Frp blips often recover within 1-2s.
                var delay = TimeSpan.FromSeconds(1);
                var maxDelay = TimeSpan.FromSeconds(20);
                while (true)
                {
                    await Task.Delay(delay);
                    try
                    {
                        await EnsureConnectedAsync();
                        await RestoreAccountAfterReconnectAsync();
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
                // Complete pending TCS on the receive thread. Payload is already a plain
                // string (no WinRT JSON affinity). Completing via RunOnUi can delay or
                // race with connection teardown and leave callers hanging until timeout.
                if (!string.IsNullOrEmpty(error))
                {
                    if (_pending.TryRemove(id, out var tcsErr))
                        tcsErr.TrySetException(new Exception(error));
                }
                else
                {
                    string dataStr;
                    try
                    {
                        if (!frame.ContainsKey("data") || frame.GetNamedValue("data") == null
                            || frame.GetNamedValue("data").ValueType == JsonValueType.Null)
                            dataStr = "null";
                        else
                            dataStr = frame.GetNamedValue("data").Stringify();
                    }
                    catch
                    {
                        dataStr = "null";
                    }
                    if (_pending.TryRemove(id, out var tcsOk))
                        tcsOk.TrySetResult(dataStr);
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
            else if (type == "conversationFlagsChanged")
            {
                var data = frame.GetNamedObject("data");
                var info = new ConversationFlagsChangedInfo
                {
                    ConversationId = Str(data, "conversationId"),
                    IsPinned = data.GetNamedBoolean("isPinned", false),
                    IsMuted = data.GetNamedBoolean("isMuted", false),
                };
                if (!string.IsNullOrEmpty(info.ConversationId))
                {
                    // Keep local toast gate aligned even before the UI list applies the row.
                    NotificationMuteGate.SetConversationMuted(info.ConversationId, info.IsMuted);
                    if (info.IsMuted) UnreadBadgeStore.Clear(info.ConversationId);
                    RunOnUi(() => ConversationFlagsChanged?.Invoke(this, info));
                }
            }
            else if (type == "conversationRead")
            {
                var data = frame.GetNamedObject("data");
                var convId = Str(data, "conversationId");
                if (!string.IsNullOrEmpty(convId))
                {
                    UnreadBadgeStore.Clear(convId);
                    var info = new ConversationReadInfo
                    {
                        ConversationId = convId,
                        LastReadAt = Str(data, "lastReadAt"),
                    };
                    RunOnUi(() => ConversationRead?.Invoke(this, info));
                }
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
            else if (type == "sessionDataUpdated")
            {
                // Low: list rebuilds must not starve navigation/touch when populate finishes.
                RunOnUi(() => SessionDataUpdated?.Invoke(this, EventArgs.Empty),
                    Windows.UI.Core.CoreDispatcherPriority.Low);
            }
            else if (type == "spaceFeedUpdated")
            {
                var data = frame.GetNamedObject("data");
                // Parse hasMore from push data so the VM can show/hide "load more" button.
                if (data != null && data.ContainsKey("hasMore"))
                    SpaceFeedHasMore = data.GetNamedBoolean("hasMore", true);
                RunOnUi(() => SpaceFeedUpdated?.Invoke(this, EventArgs.Empty),
                    Windows.UI.Core.CoreDispatcherPriority.Low);
            }
        }

        private void RunOnUi(
            Action action,
            Windows.UI.Core.CoreDispatcherPriority priority = Windows.UI.Core.CoreDispatcherPriority.Normal)
        {
            if (action == null) return;
            var d = _dispatcher;
            // Always wrap: a throwing event handler on the UI thread would tear down the
            // process via the unhandled exception path (especially sessionDataUpdated /
            // MessageReceived fan-out into MainViewModel list rebuilds).
            void Safe()
            {
                try { action(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("RunOnUi handler: " + ex);
                }
            }

            if (d != null && !d.HasThreadAccess)
            {
                var ignore = d.RunAsync(priority, Safe);
            }
            else
            {
                Safe();
            }
        }

        private async Task<string> RequestAsync(string type, Action<JsonObject> fill, int timeoutSeconds = 10)
        {
            await EnsureConnectedAsync();
            // "configureAccount" is the binding request itself; every other operation on a
            // re-created socket waits for that binding first. This also covers an on-demand
            // reconnect started by a Send before the background reconnect loop wakes up.
            if (!string.Equals(type, "configureAccount", StringComparison.Ordinal))
                await EnsureAccountBoundForCurrentConnectionAsync();

            var id = Guid.NewGuid().ToString("N");
            var req = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(id),
                ["type"] = JsonValue.CreateStringValue(type),
            };
            fill?.Invoke(req);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            await _writeLock.WaitAsync();
            try
            {
                var writer = _writer;
                if (writer == null) throw new InvalidOperationException("connection closed");
                writer.WriteString(req.Stringify());
                await writer.StoreAsync();
            }
            catch (Exception ex)
            {
                // Write failed -- the connection is dead (e.g. writing into a half-open
                // stream after an unnoticed drop). Tear down fully so the next call
                // creates a fresh MessageWebSocket (required after any write/close error).
                _pending.TryRemove(id, out _);
                HandleConnectionDead();
                throw new Exception(FormatSocketError("发送请求失败（" + type + "）", ex), ex);
            }
            finally { _writeLock.Release(); }

            // Image/highway uploads to Tencent routinely exceed the default 10s used for
            // lightweight get*/send-text RPCs; callers of media sends pass a longer budget.
            var seconds = timeoutSeconds < 1 ? 10 : timeoutSeconds;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            using (cts.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t))
                    t.TrySetException(new TimeoutException("请求超时（" + type + "）"));
            }))
            {
                return await tcs.Task;
            }
        }

    }
}
