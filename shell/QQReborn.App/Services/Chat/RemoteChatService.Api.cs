using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
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
                        Remark = Str(o, "remark"),
                        AvatarPath = Str(o, "avatarPath"),
                        Signature = Str(o, "signature"),
                        Online = o.GetNamedBoolean("online", false),
                    });
                }
                return (IReadOnlyList<Contact>)list;
            });
        }


        public async Task<bool> MarkAllAsReadAsync()
        {
            var data = JsonObject.Parse(await RequestAsync("markAllAsRead", null));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<bool> ConfigureAccountAsync(string expectedUin = null)
        {
            var raw = await RequestAsync("configureAccount", r =>
            {
                r["expectedUin"] = JsonValue.CreateStringValue(expectedUin ?? "");
            });
            JsonObject data;
            if (string.IsNullOrEmpty(raw) || raw == "null" || !JsonObject.TryParse(raw, out data))
                return false;
            var accepted = data.GetNamedBoolean("accepted", false);
            if (accepted)
            {
                lock (_accountGate)
                {
                    _configuredExpectedUin = expectedUin ?? "";
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
            string expectedUin;
            lock (_accountGate)
            {
                if (!_accountConfigured) return;
                expectedUin = _configuredExpectedUin;
            }

            var raw = await RequestAsync("configureAccount", r =>
            {
                r["expectedUin"] = JsonValue.CreateStringValue(expectedUin ?? "");
            }, timeoutSeconds: 20);
            if (string.IsNullOrEmpty(raw) || raw == "null"
                || !JsonObject.TryParse(raw, out var data)
                || !data.GetNamedBoolean("accepted", false))
                throw new InvalidOperationException("网关重连后无法恢复 QQ 会话");
            lock (_accountGate) _accountBoundForCurrentConnection = true;
        }

        // ---- channel / guild ----

        /// <summary>Send a channel.* command through the shared WebSocket transport.</summary>
    }
}
