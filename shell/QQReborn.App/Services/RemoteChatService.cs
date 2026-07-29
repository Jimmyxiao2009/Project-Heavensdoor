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

    /// <summary>Pushed when any client changes pin/mute via setConversationFlags.</summary>
    public class ConversationFlagsChangedInfo
    {
        public string ConversationId { get; set; }
        public bool IsPinned { get; set; }
        public bool IsMuted { get; set; }
    }

    public class ConversationReadInfo
    {
        public string ConversationId { get; set; }
        public string LastReadAt { get; set; }
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

            // Users often paste "ws://host:port/ws" or "host:port" into the host box.
            // Strip scheme/path and, when present, take an embedded port.
            host = NormalizeServerHost(host, ref port);
            return "ws://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/ws";
        }

        /// <summary>
        /// Accepts bare host, host:port, ws(s)://host[:port][/path], http(s)://...
        /// Returns host only; updates <paramref name="port"/> when the input embeds one.
        /// </summary>
        internal static string NormalizeServerHost(string raw, ref int port)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultServerHost;
            var s = raw.Trim();

            // Strip surrounding brackets from IPv6 literals: [2001:db8::1]
            // (full bracketed form handled below when a port is also present).
            if (s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal) && s.Length > 2)
                return s.Substring(1, s.Length - 2);

            // scheme://...
            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
                s = s.Substring(schemeIdx + 3);

            // drop path/query/fragment
            var cut = s.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return DefaultServerHost;

            // [ipv6]:port
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                var close = s.IndexOf(']');
                if (close > 1)
                {
                    var hostPart = s.Substring(1, close - 1);
                    if (close + 1 < s.Length && s[close + 1] == ':')
                    {
                        var portPart = s.Substring(close + 2);
                        int embedded;
                        if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out embedded)
                            && embedded > 0 && embedded < 65536)
                            port = embedded;
                    }
                    return string.IsNullOrWhiteSpace(hostPart) ? DefaultServerHost : hostPart;
                }
            }

            // host:port (avoid splitting IPv6 which contains multiple ':')
            var lastColon = s.LastIndexOf(':');
            if (lastColon > 0 && s.IndexOf(':') == lastColon)
            {
                var hostPart = s.Substring(0, lastColon).Trim();
                var portPart = s.Substring(lastColon + 1).Trim();
                int embedded;
                if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out embedded)
                    && embedded > 0 && embedded < 65536)
                {
                    port = embedded;
                    return string.IsNullOrWhiteSpace(hostPart) ? DefaultServerHost : hostPart;
                }
            }

            return s;
        }

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
        private string _configuredSignUrl = "";
        private string _configuredSignToken = "";
        private string _configuredSignUin = "";
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
                RunOnUi(() => SessionDataUpdated?.Invoke(this, EventArgs.Empty));
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
            var payload = await RequestAsync("getConversations", null);
            // A large account can return hundreds or thousands of sessions. Do JSON parsing
            // and model construction off the UI context; MainViewModel switches back only for
            // the small, bound-collection merge.
            return await Task.Run(() =>
            {
                var arr = JsonArray.Parse(payload);
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
                        LastReadAt = Str(o, "lastReadAt"),
                        Announcement = Str(o, "announcement"),
                        IsPinned = o.GetNamedBoolean("isPinned", false),
                        IsMuted = o.GetNamedBoolean("isMuted", false),
                    });
                }
                return (IReadOnlyList<ChatConversation>)list;
            });
        }

        /// <summary>Set pin/mute flags on the backend. Omitted (null) flags are left alone
        /// server-side; on success we don't mutate a local model here -- the caller owns that.</summary>
        public async Task SetConversationFlagsAsync(string conversationId, bool? isPinned, bool? isMuted)
        {
            await RequestAsync("setConversationFlags", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId ?? "");
                if (isPinned.HasValue) r["isPinned"] = JsonValue.CreateBooleanValue(isPinned.Value);
                if (isMuted.HasValue) r["isMuted"] = JsonValue.CreateBooleanValue(isMuted.Value);
            });
        }

        /// <summary>Clear server-side unread while the user is viewing this conversation
        /// (so live messages don't leave a badge after they go back to the list).</summary>
        public async Task MarkConversationReadAsync(string conversationId, string lastReadAt = null)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(lastReadAt))
                    lastReadAt = DateTimeOffset.UtcNow.ToString("o");
                await RequestAsync("markConversationRead", r =>
                {
                    r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                    r["lastReadAt"] = JsonValue.CreateStringValue(lastReadAt);
                });
            }
            catch
            {
                // Best-effort.
            }
        }

        /// <summary>Connect + auth as soon as the app starts so offline push/unread can flow
        /// before the user opens the conversation list.</summary>
        public void StartAutoConnect()
        {
            // Prefer a soft ensure when already up; ForceReconnect tears down a healthy socket.
            var ignored = AutoConnectAsync();
        }

        private async Task AutoConnectAsync()
        {
            try
            {
                if (_connected) return;
                var hadPreviousConnection = _everConnected;
                await EnsureConnectedAsync();
                await RestoreAccountAfterReconnectAsync();
                // The app starts the transport before MainViewModel binds the QQ
                // account. Do not announce that initial socket as a reconnect: doing
                // so can make an early refresh race configureAccount and briefly
                // replace the real conversation list with an empty one.
                if (hadPreviousConnection || IsAccountConfigured())
                    RunOnUi(() => Reconnected?.Invoke(this, EventArgs.Empty));
            }
            catch
            {
                TryStartReconnectLoop();
            }
        }

        public async Task<IReadOnlyList<Contact>> GetContactsAsync()
        {
            var payload = await RequestAsync("getContacts", null);
            return await Task.Run(() =>
            {
                var arr = JsonArray.Parse(payload);
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
                return (IReadOnlyList<Contact>)list;
            });
        }

        public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, bool localOnly = false)
        {
            // Opening a chat may trigger a one-shot cloud pull (30s). Search uses localOnly
            // and only hits the session cache (fast, no sign/history storm).
            var payload = await RequestAsync("getMessages", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                if (localOnly) r["localOnly"] = JsonValue.CreateBooleanValue(true);
            }, timeoutSeconds: localOnly ? 10 : 30);
            return await Task.Run(() =>
            {
                var arr = JsonArray.Parse(payload);
                var list = new List<ChatMessage>();
                foreach (var n in arr) list.Add(ParseMessage(n.GetObject()));
                return (IReadOnlyList<ChatMessage>)list;
            });
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
            var encodedChars = 0;
            foreach (var path in imagePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var b64 = await EncodeImageForSendAsync(path);
                if (string.IsNullOrEmpty(b64)) continue;
                encodedChars += b64.Length;
                if (encodedChars > MaxCombinedImageBase64Chars)
                    throw new InvalidOperationException("图片合计过大，请分开发送");
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

        public Task<ChatMessage> SendLocationAsync(string conversationId, string placeName, string address, string thumb,
            double latitude = 0, double longitude = 0)
            => SendAsync(conversationId, "Location", placeName, null, null, 0, r =>
            {
                if (placeName != null) r["placeName"] = JsonValue.CreateStringValue(placeName);
                if (address != null) r["address"] = JsonValue.CreateStringValue(address);
                if (thumb != null) r["thumb"] = JsonValue.CreateStringValue(thumb);
                r["latitude"] = JsonValue.CreateNumberValue(latitude);
                r["longitude"] = JsonValue.CreateNumberValue(longitude);
            });

        public async Task<IReadOnlyList<string>> GetFavoriteStickersAsync()
        {
            var raw = await RequestAsync("getFavoriteStickers", r =>
                r["count"] = JsonValue.CreateNumberValue(48), timeoutSeconds: 30);
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw) || raw == "null") return result;
            var root = JsonValue.Parse(raw);
            JsonArray arr = null;
            if (root.ValueType == JsonValueType.Array)
                arr = root.GetArray();
            else if (root.ValueType == JsonValueType.Object)
            {
                var obj = root.GetObject();
                if (obj.ContainsKey("stickers") && obj["stickers"].ValueType == JsonValueType.Array)
                    arr = obj.GetNamedArray("stickers");
                else if (obj.ContainsKey("data") && obj["data"].ValueType == JsonValueType.Array)
                    arr = obj.GetNamedArray("data");
            }
            if (arr == null) return result;
            foreach (var item in arr)
            {
                var path = item.ValueType == JsonValueType.String
                    ? item.GetString()
                    : item.ValueType == JsonValueType.Object ? Str(item.GetObject(), "url") : "";
                if (!string.IsNullOrEmpty(path) && !result.Contains(path)) result.Add(path);
            }
            return result;
        }

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

        public async Task<ChatMessage> ForwardMessagesAsync(string targetConversationId, IReadOnlyList<string> messageIds)
        {
            var data = JsonObject.Parse(await RequestAsync("forwardMany", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(targetConversationId);
                var ids = new JsonArray();
                if (messageIds != null)
                    foreach (var id in messageIds)
                        if (!string.IsNullOrEmpty(id)) ids.Add(JsonValue.CreateStringValue(id));
                r["messageIds"] = ids;
            }, timeoutSeconds: 60));
            return ParseMessage(data);
        }

        public async Task<IReadOnlyList<ForwardEntry>> GetForwardDetailsAsync(string messageId)
        {
            var result = new List<ForwardEntry>();
            var raw = await RequestAsync("getForwardDetails", r =>
                r["messageId"] = JsonValue.CreateStringValue(messageId), timeoutSeconds: 30);
            if (string.IsNullOrEmpty(raw) || raw == "null") return result;
            var data = JsonObject.Parse(raw);
            if (data == null || !data.ContainsKey("entries")) return result;
            foreach (var item in data.GetNamedArray("entries"))
            {
                if (item.ValueType != JsonValueType.Object) continue;
                var o = item.GetObject();
                result.Add(new ForwardEntry
                {
                    SenderName = Str(o, "senderName"),
                    Text = Str(o, "text"),
                    ImagePath = Str(o, "imagePath"),
                });
            }
            return result;
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
            return data.GetNamedBoolean("recalled", false) || data.GetNamedBoolean("ok", false);
        }

        /// <summary>Leaves a group conversation. Returns data.left as reported by the server
        /// (e.g. false if the backend can't perform the action, same honesty convention as
        /// AcceptFriendRequestAsync's handled flag).</summary>
        public async Task<bool> QuitGroupAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("quitGroup",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            return data.GetNamedBoolean("left", false) || data.GetNamedBoolean("ok", false);
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
        /// Kicks off real-account login on the backend (RealServer only). The fake demo
        /// server has no "configureAccount" handler and echoes data:null -- note that
        /// JsonObject.Parse("null") THROWS in Windows.Data.Json (top level must be an
        /// object), so that case is checked explicitly and reported as a graceful false.
        /// Login itself proceeds in the background; watch QrCodeReceived/LoginStatusChanged
        /// for progress.
        /// </summary>
        public async Task<bool> ConfigureAccountAsync(string signUrl, string signToken, string signUin)
        {
            // NapCat path: leave signUin empty unless the user intentionally set one —
            // a stale QQ number in settings causes a hard mismatch error even when NapCat
            // is correctly logged in as a different account.
            var raw = await RequestAsync("configureAccount", r =>
            {
                r["signUrl"] = JsonValue.CreateStringValue(signUrl ?? "");
                r["signToken"] = JsonValue.CreateStringValue(signToken ?? "");
                r["signUin"] = JsonValue.CreateStringValue(signUin ?? "");
            });
            JsonObject data;
            if (string.IsNullOrEmpty(raw) || raw == "null" || !JsonObject.TryParse(raw, out data))
                return false;
            var accepted = data.GetNamedBoolean("accepted", false);
            if (accepted)
            {
                lock (_accountGate)
                {
                    _configuredSignUrl = signUrl ?? "";
                    _configuredSignToken = signToken ?? "";
                    _configuredSignUin = signUin ?? "";
                    _accountConfigured = true;
                    _accountBoundForCurrentConnection = true;
                }
            }
            return accepted;
        }

        private bool IsAccountConfigured()
        {
            lock (_accountGate) return _accountConfigured;
        }

        private async Task EnsureAccountBoundForCurrentConnectionAsync()
        {
            lock (_accountGate)
            {
                if (!_accountConfigured || _accountBoundForCurrentConnection) return;
            }

            await _accountBindLock.WaitAsync();
            try
            {
                lock (_accountGate)
                {
                    if (!_accountConfigured || _accountBoundForCurrentConnection) return;
                }
                await RestoreAccountAfterReconnectAsync();
            }
            finally
            {
                _accountBindLock.Release();
            }
        }

        /// <summary>Re-bind the backend session created for the previous socket.
        /// SessionHub is connection-scoped today, so a transport reconnect otherwise
        /// leaves the new backend at UIN 0 and the next refresh looks like an empty account.</summary>
        private async Task RestoreAccountAfterReconnectAsync()
        {
            string signUrl;
            string signToken;
            string signUin;
            lock (_accountGate)
            {
                if (!_accountConfigured) return;
                signUrl = _configuredSignUrl;
                signToken = _configuredSignToken;
                signUin = _configuredSignUin;
            }

            var raw = await RequestAsync("configureAccount", r =>
            {
                r["signUrl"] = JsonValue.CreateStringValue(signUrl);
                r["signToken"] = JsonValue.CreateStringValue(signToken);
                r["signUin"] = JsonValue.CreateStringValue(signUin);
            }, timeoutSeconds: 20);
            if (string.IsNullOrEmpty(raw) || raw == "null"
                || !JsonObject.TryParse(raw, out var data)
                || !data.GetNamedBoolean("accepted", false))
                throw new InvalidOperationException("网关重连后无法恢复 QQ 会话");
            lock (_accountGate) _accountBoundForCurrentConnection = true;
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
                PlaceLatitude = o.GetNamedNumber("latitude", 0),
                PlaceLongitude = o.GetNamedNumber("longitude", 0),
                ReplyToSender = Str(o, "replyToSender"),
                ReplyToText = Str(o, "replyToText"),
                FileName = Str(o, "fileName"),
                FileSize = Str(o, "fileSize"),
                FileId = Str(o, "fileId"),
                Time = ParseTime(Str(o, "time")),
                State = MessageState.Sent,
            };

            if (type == "Forward" && o.ContainsKey("forwardEntries")
                && o.GetNamedValue("forwardEntries").ValueType == JsonValueType.Array)
            {
                foreach (var entry in o.GetNamedArray("forwardEntries"))
                {
                    if (entry.ValueType != JsonValueType.Object) continue;
                    var fo = entry.GetObject();
                    msg.ForwardEntries.Add(new ForwardEntry
                    {
                        SenderName = Str(fo, "senderName"),
                        Text = Str(fo, "text"),
                        ImagePath = Str(fo, "imagePath"),
                    });
                }
            }

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
                case "Forward": return MessageContentType.Forward;
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
        // RealServer caps one WebSocket frame at 2 MiB. Leave room for JSON, captions,
        // mentions, and the base64 expansion so a normal phone photo does not fail only
        // after the user has waited through the upload.
        private const uint MaxChatImageEdge = 1024;
        private const int MaxCombinedImageBase64Chars = 1_400_000;

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

            if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var http = new Windows.Web.Http.HttpClient();
                var buffer = await http.GetBufferAsync(new Uri(imagePath));
                var cached = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "favorite_sticker_" + Guid.NewGuid().ToString("N") + ".img",
                    CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteBufferAsync(cached, buffer);
                imagePath = "ms-appdata:///local/" + cached.Name;
            }

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
                        ["ImageQuality"] = new BitmapTypedValue(0.72f, Windows.Foundation.PropertyType.Single)
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
