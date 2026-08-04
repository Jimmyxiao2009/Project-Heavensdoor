using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public partial class ConversationViewModel
    {

        public void DeleteMessage(ChatMessage msg)
        {
            if (msg != null) Messages.Remove(msg);
        }

        /// <summary>
        /// Recall an outgoing message. Against the mock backend this stays a purely local,
        /// always-succeeds swap (unchanged demo behavior). Against a remote backend it first
        /// asks the server via RemoteChatService.RecallMessageAsync -- only past the recall
        /// time window or otherwise unsupported by the account -- and only mutates the UI on
        /// success, so a failed recall leaves the original bubble intact instead of lying to
        /// the user (and to the peer, who never saw a recall on the wire either).
        /// Called from an async void context (right-click menu handler), so this method
        /// itself must never let an exception escape.
        /// </summary>
        public async Task RecallMessageAsync(ChatMessage msg)
        {
            if (msg == null) return;
            if (_chat is IGatewayService remote)
            {
                bool recalled;
                try
                {
                    recalled = await remote.RecallMessageAsync(ConversationId, msg.Id);
                }
                catch (Exception)
                {
                    recalled = false;
                }
                if (!recalled)
                {
                    AppendSystem("撤回失败（超过时限或不支持）");
                    return;
                }
            }
            ApplyLocalRecall(msg);
        }

        /// <summary>Swap a message for a centered "你撤回了一条消息" system line at the same spot.
        /// Pure local UI mutation -- callers decide whether the backend has already confirmed
        /// the recall (see RecallMessageAsync).</summary>
        private void ApplyLocalRecall(ChatMessage msg)
        {
            var index = Messages.IndexOf(msg);
            if (index < 0) return;

            var system = new ChatMessage
            {
                // Fresh id so a later server echo/forward keyed by the original id can't collide.
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = msg.ConversationId,
                Direction = MessageDirection.Outgoing,
                ContentType = MessageContentType.System,
                Text = "你撤回了一条消息",
                Time = msg.Time,
                ShowTimeDivider = msg.ShowTimeDivider
            };

            Messages.RemoveAt(index);
            Messages.Insert(index, system);
            UpdateReadState();
        }

        public async Task SendAsync()
        {
            if (!CanSend) return;
            IsSending = true;
            var conversationId = ConversationId;
            var text = (_draft ?? string.Empty).Trim();
            var images = new System.Collections.Generic.List<string>(PendingImages);
            var mentions = new System.Collections.Generic.List<MentionInfo>(PendingMentions);
            var replyTarget = _replyTarget;
            // Clear optimistically for a snappy UI, but restore draft/images if the send fails
            // (e.g. the remote backend timed out / dropped) so the user doesn't lose typing.
            Draft = string.Empty;
            ClearPendingImages();

            string mentionsJson = null;
            if (mentions.Count > 0)
            {
                var arr = new Windows.Data.Json.JsonArray();
                foreach (var m in mentions)
                {
                    var o = new Windows.Data.Json.JsonObject();
                    o["uin"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.Uin);
                    o["display"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Display);
                    arr.Add(o);
                }
                mentionsJson = arr.Stringify();
                PendingMentions.Clear();
            }

            try
            {
                ChatMessage sent;
                if (images.Count > 0)
                {
                    // One protocol message: optional caption + 1..N images (图文混排).
                    sent = await _chat.SendMixedAsync(
                        conversationId,
                        text,
                        images,
                        replyTarget != null ? replyTarget.Id : null,
                        mentionsJson);
                }
                else if (_chat is IGatewayService remote && replyTarget != null)
                {
                    // Against a real/remote backend, embed an actual reply-quote in the protocol
                    // message (so the recipient's real client sees it and it survives a reload)
                    // instead of just stamping it on our own local copy after the fact.
                    sent = await remote.SendTextWithReplyAsync(conversationId, text, replyTarget.Id, mentionsJson);
                }
                else
                {
                    sent = await _chat.SendTextAsync(conversationId, text, mentionsJson);
                }
                if (!IsCurrentConversation(conversationId)) return;
                ApplyReplyTarget(sent, replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                if (!IsCurrentConversation(conversationId)) return;

                // A user can start another message while the request is pending. Only restore
                // the failed composer snapshot when that newer input has not changed it.
                bool composerChangedWhileSending = !string.IsNullOrEmpty(_draft)
                    || PendingImages.Count > 0
                    || PendingMentions.Count > 0;
                if (!composerChangedWhileSending)
                {
                    Draft = text;
                    foreach (var p in images) AttachPendingImage(p);
                    PendingMentions.AddRange(mentions);
                }
                // Surface the real reason (e.g. sign 401 / seq=0 / not-online) instead of a
                // silent draft restore that looks like "can't send to groups".
                var detail = string.IsNullOrEmpty(ex.Message)
                    ? "请稍后重试"
                    : Services.RemoteChatService.FormatSocketError("发送失败", ex);
                // Avoid double prefix when FormatSocketError already includes it.
                if (detail.StartsWith("发送失败", System.StringComparison.Ordinal))
                    AppendSystem(detail);
                else
                    AppendSystem("发送失败（" + detail + "）");
            }
            finally
            {
                IsSending = false;
            }
        }

        // The media send paths must not let a backend failure escape: their callers are
        // async void page event handlers, and an unhandled exception there kills the app
        // (the real server rejects image/sticker/voice with unsupported-content for now).
        public async Task SendImageAsync(string imagePath)
        {
            // Stage for 图文混排 instead of immediately sending a bare image message.
            // User can type a caption and press 发送; pure-image still works with empty draft.
            AttachPendingImage(imagePath);
            await Task.CompletedTask;
        }

        public async Task SendStickerAsync(string stickerPath)
        {
            if (string.IsNullOrEmpty(stickerPath)) return;
            try
            {
                var sent = await _chat.SendStickerAsync(ConversationId, stickerPath);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("发送失败：表情未发出（" + detail + "）");
            }
        }

        public async Task SendVoiceAsync(string audioPath, int seconds)
        {
            if (string.IsNullOrEmpty(audioPath)) return;
            try
            {
                var sent = await _chat.SendVoiceAsync(ConversationId, audioPath, seconds);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("发送失败：语音未发出（" + detail + "）");
            }
        }

        public async Task SendLocationAsync(string placeName, string address, string thumb,
            double latitude = 0, double longitude = 0)
        {
            try
            {
                var sent = await _chat.SendLocationAsync(ConversationId, placeName, address, thumb, latitude, longitude);
                ApplyReplyTarget(sent, _replyTarget);
                Append(sent);
            }
            catch (Exception) { AppendSystem("发送失败：位置没有发出去"); }
        }

        /// <summary>Append a local system notice (撤回 / 戳一戳 / etc).</summary>
        public void AppendSystem(string text)
        {
            var msg = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = ConversationId,
                Direction = MessageDirection.Outgoing,
                ContentType = MessageContentType.System,
                Text = text,
                Time = DateTimeOffset.Now
            };
            Append(msg);
        }

        /// <summary>
        /// Stamp the reply quote on a freshly-sent message. Takes the reply target that was
        /// CAPTURED before the send await (not the live field): the user can retarget or
        /// clear the reply banner while the send is in flight, and the bubble must reflect
        /// what actually went out on the wire. When the backend already returned reply
        /// metadata (RealServer echoes replyToSender/replyToText), keep the authoritative
        /// server values instead of clobbering them with a local guess -- otherwise the
        /// in-session bubble and the reloaded-from-server one would render differently.
        /// </summary>
        private void ApplyReplyTarget(ChatMessage sent, ChatMessage replyTarget)
        {
            if (sent == null || replyTarget == null) return;
            if (string.IsNullOrEmpty(sent.ReplyToSender))
            {
                sent.ReplyToSender = string.IsNullOrEmpty(replyTarget.SenderName) ? "我" : replyTarget.SenderName;
                sent.ReplyToText = replyTarget.Text;
            }
            // Only clear the pending banner if the user hasn't picked a new target mid-send.
            if (ReferenceEquals(_replyTarget, replyTarget)) ReplyTarget = null;
        }

        /// <summary>Append a message produced by a forward action that targets this conversation.</summary>
        public void AppendForwarded(ChatMessage msg) => Append(msg);

        /// <summary>Called on the UI thread by the page when a message arrives for this conversation.</summary>
        public void OnIncoming(ChatMessage msg)
        {
            if (msg == null || msg.ConversationId != ConversationId) return;
            Append(msg);
            UpdateReadState();
        }

        private void Append(ChatMessage msg)
        {
            if (msg == null || Messages.Contains(msg)) return;
            // Same wire message can arrive twice as two distinct objects: once from the
            // send-response parse and once from the messageReceived broadcast (the server
            // can only fully prevent that on its side when the echo loses the race).
            if (!string.IsNullOrEmpty(msg.Id))
            {
                foreach (var existing in Messages)
                    if (existing.Id == msg.Id) return;
            }
            var prev = Messages.Count > 0 ? Messages[Messages.Count - 1] : null;
            ApplyDivider(prev, msg);
            Messages.Add(msg);
            UpdateReadState();
        }

        /// <summary>Show the read-receipt label only on the latest outgoing (non-system) message.</summary>
        private void UpdateReadState()
        {
            ChatMessage latestOutgoing = null;
            foreach (var m in Messages)
            {
                m.ShowReadState = false;
                if (m.IsOutgoing && !m.IsSystem) latestOutgoing = m;
            }
            if (latestOutgoing != null) latestOutgoing.ShowReadState = true;
        }

        private static void ApplyDivider(ChatMessage prev, ChatMessage msg)
        {
            msg.ShowTimeDivider = prev == null || (msg.Time - prev.Time) > DividerGap;
        }
    }
}
