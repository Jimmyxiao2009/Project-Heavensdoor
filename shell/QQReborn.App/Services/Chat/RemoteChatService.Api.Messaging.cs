using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
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

        public async Task<bool> SendNudgeAsync(string conversationId, long targetUin)
        {
            var data = JsonObject.Parse(await RequestAsync("nudge", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["targetUin"] = JsonValue.CreateNumberValue(targetUin);
            }));
            return data.GetNamedBoolean("sent", false);
        }


        public async Task<bool> SetEssenceAsync(string messageId, bool set = true)
        {
            var data = JsonObject.Parse(await RequestAsync("setEssence", r =>
            {
                r["messageId"] = JsonValue.CreateStringValue(messageId ?? "");
                r["set"] = JsonValue.CreateBooleanValue(set);
            }));
            return data != null && data.GetNamedBoolean("ok", false);
        }


        public async Task<IReadOnlyList<string>> GetEssenceSummariesAsync(string conversationId)
        {
            var data = JsonObject.Parse(await RequestAsync("getEssenceList",
                r => r["conversationId"] = JsonValue.CreateStringValue(conversationId)));
            var list = new List<string>();
            var arr = data?.GetNamedArray("messages");
            if (arr == null) return list;
            foreach (var n in arr)
            {
                var o = n.GetObject();
                var who = Str(o, "senderName");
                var content = Str(o, "content");
                list.Add(string.IsNullOrEmpty(who) ? content : who + ": " + content);
            }
            return list;
        }


        public async Task<string> FetchPttTextAsync(string messageId)
        {
            var data = JsonObject.Parse(await RequestAsync("fetchPttText",
                r => r["messageId"] = JsonValue.CreateStringValue(messageId ?? ""),
                timeoutSeconds: 45));
            return data != null ? Str(data, "text") : null;
        }


        public async Task<ChatMessage> SendVideoAsync(string conversationId, string videoPath)
        {
            var base64 = await EncodeFileBase64Async(videoPath);
            var data = JsonObject.Parse(await RequestAsync("send", r =>
            {
                r["conversationId"] = JsonValue.CreateStringValue(conversationId);
                r["contentType"] = JsonValue.CreateStringValue("Video");
                r["fileBase64"] = JsonValue.CreateStringValue(base64);
                r["fileName"] = JsonValue.CreateStringValue(System.IO.Path.GetFileName(videoPath) ?? "video.mp4");
                r["voiceSeconds"] = JsonValue.CreateNumberValue(0);
            }, timeoutSeconds: 120));
            return ParseMessage(data);
        }

        /// <summary>Uploads a new avatar image (base64-encoded) for the logged-in user. Returns
        /// data.ok as reported by the server.</summary>

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

    }
}
