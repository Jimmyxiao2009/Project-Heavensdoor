using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

public sealed partial class NapCatSessionManager
{

    public async Task<(JsonObject? data, string? error)> SendAsync(
        string conversationId, string text, string? replyToId = null,
        string contentType = "Text", string? placeName = null, string? address = null, string? thumb = null,
        string? imageBase64 = null, JsonNode? imagesBase64Node = null, string? audioBase64 = null, int voiceSeconds = 0,
        string? fileBase64 = null, string? fileName = null, string? mentionsJson = null,
        double latitude = 0, double longitude = 0)
    {
        if (!_online) return (null, "not-online");
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");

        // File: prefer dedicated upload APIs (NapCat), then fall back to message segment.
        if (contentType == "File" && !string.IsNullOrEmpty(fileBase64))
            return await SendFileAsync(conversationId, kind, peer, fileBase64, fileName);

        var segments = new JsonArray();
        if (!string.IsNullOrEmpty(replyToId))
        {
            var mid = ExtractNapCatMessageId(replyToId);
            if (mid > 0)
                segments.Add(new JsonObject { ["type"] = "reply", ["data"] = new JsonObject { ["id"] = mid.ToString() } });
        }

        // Mentions: Shell sends JSON array [{ "uin": 123, "display": "@昵称" }, ...]
        var mentionParts = ParseMentions(mentionsJson);
        foreach (var m in mentionParts)
        {
            var qq = m.uin > 0 ? m.uin.ToString() : "all";
            var atName = (m.display ?? "").TrimStart('@');
            if (string.IsNullOrEmpty(atName)) atName = m.uin > 0 ? m.uin.ToString() : "全体成员";
            segments.Add(new JsonObject
            {
                ["type"] = "at",
                ["data"] = new JsonObject { ["qq"] = qq, ["name"] = atName },
            });
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = " " } });
        }

        // Caption text (strip pure display tokens already covered by at segments when possible)
        var caption = text ?? "";
        if (mentionParts.Count > 0 && !string.IsNullOrWhiteSpace(caption))
        {
            foreach (var m in mentionParts)
            {
                if (string.IsNullOrEmpty(m.display)) continue;
                caption = caption.Replace(m.display, "", StringComparison.Ordinal);
            }
            caption = caption.Trim();
        }

        if (contentType is "Text" or "Mixed" or "" || (contentType is "Image" or "Sticker" && !string.IsNullOrWhiteSpace(caption)))
        {
            if (!string.IsNullOrWhiteSpace(caption) && contentType != "Location")
                segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = caption } });
        }
        if (contentType == "Location")
        {
            if (latitude == 0 && longitude == 0)
                return (null, "location-coordinates-missing");
            segments.Add(new JsonObject
            {
                ["type"] = "location",
                ["data"] = new JsonObject
                {
                    ["lat"] = latitude,
                    ["lon"] = longitude,
                    ["title"] = placeName ?? "我的位置",
                    ["content"] = address ?? "",
                },
            });
        }

        // Images (图文混排: text/at segments above + image segments).
        // Prefer a real temp file path for NapCat — large base64:// payloads often
        // return message_id but render as empty / broken thumbnails on QQ clients.
        var images = CollectImages(imageBase64, imagesBase64Node);
        var tempImagePaths = new List<string>();
        var tempVoicePaths = new List<string>();
        var tempVideoPaths = new List<string>();
        foreach (var b64 in images)
        {
            var path = TryWriteTempMedia(b64, ".jpg");
            if (path != null) tempImagePaths.Add(path);
            var fileRef = path ?? ("base64://" + StripDataUrl(b64));
            segments.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = new JsonObject { ["file"] = fileRef },
            });
        }

        if (contentType == "Voice" && !string.IsNullOrEmpty(audioBase64))
        {
            var voicePath = TryWriteTempMedia(audioBase64, ".m4a");
            if (voicePath != null) tempVoicePaths.Add(voicePath);
            segments.Add(new JsonObject
            {
                ["type"] = "record",
                ["data"] = new JsonObject { ["file"] = voicePath ?? ("base64://" + StripDataUrl(audioBase64)) },
            });
        }

        // Video: reuse imageBase64/fileBase64 field as media payload; contentType "Video".
        if (contentType == "Video" && (!string.IsNullOrEmpty(fileBase64) || !string.IsNullOrEmpty(imageBase64)))
        {
            var b64 = !string.IsNullOrEmpty(fileBase64) ? fileBase64 : imageBase64;
            var videoPath = TryWriteTempMedia(b64!, ".mp4");
            if (videoPath != null) tempVideoPaths.Add(videoPath);
            segments.Add(new JsonObject
            {
                ["type"] = "video",
                ["data"] = new JsonObject
                {
                    ["file"] = videoPath ?? ("base64://" + StripDataUrl(b64!)),
                },
            });
        }

        if (segments.Count == 0)
            return (null, "empty-message");

        string action;
        var parameters = new JsonObject { ["message"] = segments };
        if (kind == 'g')
        {
            action = "send_group_msg";
            parameters["group_id"] = peer;
        }
        else
        {
            action = "send_private_msg";
            parameters["user_id"] = peer;
        }

        JsonNode? data;
        string? err;
        try
        {
            (data, err) = await _api.CallAsync(action, parameters);
            if (err != null) return (null, err);

            var messageId = NapCatApiClient.ReadLong(data?["message_id"]);
            var id = $"{conversationId}:{messageId}";

            // message_sent event often arrives with real CDN URLs slightly before/after this.
            // Prefer that echo so Shell gets http imagePath (not empty / base64 / temp path).
            if (messageId > 0 && images.Count > 0)
            {
                for (var wait = 0; wait < 20; wait++)
                {
                    lock (_gate)
                    {
                        if (_msgIndex.TryGetValue(id, out var echoed))
                        {
                            var echoPath = NapCatApiClient.ReadStr(echoed["imagePath"]);
                            if (echoPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                return (Clone(echoed), null);
                        }
                    }
                    await Task.Delay(50);
                }
            }

            var (wireType, wireText, imagePath, elements) = MapSegments(segments);
            if (contentType == "Location")
            {
                wireType = "Location";
                wireText = placeName ?? "我的位置";
                elements = new JsonArray
                {
                    new JsonObject { ["Type"] = "Location", ["Text"] = address ?? "" },
                };
            }
            // Never echo base64:// or local temp paths to Shell — Image control can't load them
            // (shows empty). Prefer CDN from NapCat; else empty so client can patch local path.
            imagePath = ExtractUrlFromSendResult(data) ?? SanitizeOutboundImagePath(imagePath);
            if (contentType is "Image" or "Sticker" or "Mixed" && images.Count > 0 && string.IsNullOrEmpty(imagePath))
            {
                wireType = (!string.IsNullOrWhiteSpace(text) || mentionParts.Count > 0) && images.Count > 0 ? "Mixed"
                    : images.Count > 1 ? "Mixed" : "Image";
                if (string.IsNullOrEmpty(wireText))
                    wireText = images.Count > 1 ? $"[图片×{images.Count}]" : "[图片]";
            }
            if (mentionParts.Count > 0 && string.IsNullOrEmpty(wireText))
                wireText = string.Join(" ", mentionParts.Select(m => string.IsNullOrEmpty(m.display) ? "@" + m.uin : m.display));

            ScrubNonHttpImageUrls(elements, imagePath);

            var wire = new JsonObject
            {
                ["id"] = id,
                ["conversationId"] = conversationId,
                ["senderName"] = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname,
                ["senderUin"] = _selfUin,
                ["senderAvatarPath"] = FriendAvatarUrl(_selfUin),
                ["direction"] = "Outgoing",
                ["contentType"] = wireType,
                ["text"] = wireText,
                ["imagePath"] = imagePath,
                ["placeName"] = placeName,
                ["address"] = address,
                ["thumb"] = thumb,
                ["latitude"] = latitude,
                ["longitude"] = longitude,
                ["elements"] = elements,
                ["time"] = DateTimeOffset.UtcNow.ToString("o"),
                ["state"] = "Sent",
                ["napcatMessageId"] = messageId,
            };
            EnrichConversationMeta(wire, conversationId);

            lock (_gate)
            {
                // Event may have already inserted the same id — don't duplicate.
                if (_msgIndex.TryGetValue(id, out var existing))
                    return (Clone(existing), null);
                if (!_messages.TryGetValue(conversationId, out var list))
                {
                    list = new List<JsonObject>();
                    _messages[conversationId] = list;
                }
                list.Add(wire);
                _msgIndex[id] = wire;
                BumpConversation(conversationId, wireText, incrementUnread: false);
            }

            return (Clone(wire), null);
        }
        finally
        {
            foreach (var p in tempImagePaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
            foreach (var p in tempVoicePaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
            foreach (var p in tempVideoPaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
        }
    }

    private static void ScrubNonHttpImageUrls(JsonArray? elements, string? httpFallback)
    {
        if (elements == null) return;
        foreach (var el in elements)
        {
            if (el is not JsonObject eo) continue;
            var t = NapCatApiClient.ReadStr(eo["Type"] ?? eo["type"]);
            if (!string.Equals(t, "Image", StringComparison.OrdinalIgnoreCase)) continue;
            var u = NapCatApiClient.ReadStr(eo["Url"] ?? eo["url"]);
            if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(httpFallback) && httpFallback.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                eo["Url"] = httpFallback;
            else
            {
                eo.Remove("Url");
                eo.Remove("url");
            }
        }
    }

    private static string StripDataUrl(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return b64;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            return b64[(comma + 1)..];
        return b64;
    }

    private static string? TryWriteTempMedia(string b64, string ext)
    {
        try
        {
            var raw = StripDataUrl(b64);
            var bytes = Convert.FromBase64String(raw);
            if (bytes.Length == 0) return null;
            // sniff real extension
            if (bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50) ext = ".png";
            else if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) ext = ".jpg";
            else if (bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49) ext = ".gif";
            else if (bytes.Length > 12 && bytes[0] == 0x52 && bytes[8] == 0x57) ext = ".webp";

            var dir = Path.Combine(Path.GetTempPath(), "QQReborn", "outbox");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NapCat] temp media write failed: " + ex.Message);
            return null;
        }
    }

    private static string? ExtractUrlFromSendResult(JsonNode? data)
    {
        if (data == null) return null;
        // Some NapCat builds return url / file in send result
        foreach (var key in new[] { "url", "file", "image_url", "file_url" })
        {
            var s = NapCatApiClient.ReadStr(data[key]);
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    private static string? SanitizeOutboundImagePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
        // base64:// or local temp path is useless/broken in Shell Image control
        return null;
    }

    private async Task<(JsonObject? data, string? error)> SendFileAsync(
        string conversationId, char kind, long peer, string fileBase64, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName.Trim();
        // Strip data-url prefix if Shell ever sends it.
        var b64 = fileBase64;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            b64 = b64[(comma + 1)..];

        // 1) Prefer OneBot file segment in chat (returns message_id, shows in conversation)
        var segments = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "file",
                ["data"] = new JsonObject
                {
                    ["file"] = "base64://" + b64,
                    ["name"] = name,
                },
            },
        };
        string action = kind == 'g' ? "send_group_msg" : "send_private_msg";
        var parameters = new JsonObject { ["message"] = segments };
        if (kind == 'g') parameters["group_id"] = peer;
        else parameters["user_id"] = peer;

        var (data, err) = await _api.CallAsync(action, parameters);
        if (err == null)
        {
            var messageId = NapCatApiClient.ReadLong(data?["message_id"]);
            var wire = BuildOutgoingWire(
                conversationId,
                contentType: "File",
                text: $"[文件] {name}",
                imagePath: null,
                elements: new JsonArray { new JsonObject { ["Type"] = "File", ["Text"] = name } },
                messageId: messageId,
                fileId: null);
            return (wire, null);
        }
        Console.WriteLine($"[NapCat] file segment: {err}; try upload_*_file");

        // 2) NapCat offline-file upload APIs
        string uploadAction = kind == 'g' ? "upload_group_file" : "upload_private_file";
        var uploadParams = new JsonObject
        {
            ["file"] = "base64://" + b64,
            ["name"] = name,
        };
        if (kind == 'g') uploadParams["group_id"] = peer;
        else uploadParams["user_id"] = peer;

        var (upData, upErr) = await _api.CallAsync(uploadAction, uploadParams);
        if (upErr != null)
            return (null, $"file-send-failed: segment={err}; upload={upErr}");

        var fileId = NapCatApiClient.ReadStr(upData?["file_id"] ?? upData?["id"]);
        var mid = NapCatApiClient.ReadLong(upData?["message_id"]);
        var wireOk = BuildOutgoingWire(
            conversationId,
            contentType: "File",
            text: $"[文件] {name}",
            imagePath: null,
            elements: new JsonArray
            {
                new JsonObject { ["Type"] = "File", ["Text"] = name, ["FileId"] = fileId },
            },
            messageId: mid,
            fileId: fileId);
        return (wireOk, null);
    }

    private JsonObject BuildOutgoingWire(
        string conversationId, string contentType, string text, string? imagePath, JsonArray elements,
        long messageId, string? fileId = null, JsonArray? forwardEntries = null)
    {
        var id = messageId > 0
            ? $"{conversationId}:{messageId}"
            : !string.IsNullOrEmpty(fileId)
                ? $"{conversationId}:file:{fileId}"
                : $"{conversationId}:local-{Guid.NewGuid():N}";
        var wire = new JsonObject
        {
            ["id"] = id,
            ["conversationId"] = conversationId,
            ["senderName"] = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname,
            ["senderUin"] = _selfUin,
            ["senderAvatarPath"] = FriendAvatarUrl(_selfUin),
            ["direction"] = "Outgoing",
            ["contentType"] = contentType,
            ["text"] = text,
            ["imagePath"] = imagePath,
            ["elements"] = elements,
            ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            ["state"] = "Sent",
            ["napcatMessageId"] = messageId,
        };
        if (!string.IsNullOrEmpty(fileId)) wire["fileId"] = fileId;
        if (forwardEntries != null) wire["forwardEntries"] = forwardEntries;
        EnrichConversationMeta(wire, conversationId);
        lock (_gate)
        {
            if (!_messages.TryGetValue(conversationId, out var list))
            {
                list = new List<JsonObject>();
                _messages[conversationId] = list;
            }
            InsertMessageInTimeOrder(list, wire);
            _msgIndex[id] = wire;
            BumpConversation(conversationId, text, incrementUnread: false);
        }
        return Clone(wire);
    }

    private static List<(long uin, string display)> ParseMentions(string? mentionsJson)
    {
        var list = new List<(long uin, string display)>();
        if (string.IsNullOrWhiteSpace(mentionsJson)) return list;
        try
        {
            var node = JsonNode.Parse(mentionsJson);
            if (node is not JsonArray arr) return list;
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var uin = NapCatApiClient.ReadLong(o["uin"] ?? o["qq"] ?? o["user_id"]);
                var display = NapCatApiClient.ReadStr(o["display"] ?? o["name"] ?? o["text"]);
                var isAll = uin <= 0 && (
                    string.Equals(display, "@all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, "@全体成员", StringComparison.Ordinal)
                    || string.Equals(display, "全体成员", StringComparison.Ordinal)
                    || (display != null && display.IndexOf("全体", StringComparison.Ordinal) >= 0));
                if (uin <= 0 && !isAll)
                    continue;
                if (isAll)
                {
                    uin = 0;
                    if (string.IsNullOrEmpty(display)) display = "@全体成员";
                }
                if (string.IsNullOrEmpty(display) && uin > 0) display = "@" + uin;
                list.Add((uin, display ?? ""));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NapCat] parse mentions: " + ex.Message);
        }
        return list;
    }

    public async Task<(JsonObject? data, string? error)> ForwardAsync(string conversationId, string messageId)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");
        var mid = ExtractNapCatMessageId(messageId);
        if (mid <= 0) return (null, "invalid-message-id");

        // Resolve source bubble (for caption + custom-node fallback).
        JsonObject? src = null;
        if (!_msgIndex.TryGetValue(messageId, out src))
        {
            foreach (var kv in _msgIndex)
            {
                if (kv.Key.EndsWith(":" + mid, StringComparison.Ordinal)
                    || NapCatApiClient.ReadLong(kv.Value["napcatMessageId"]) == mid)
                {
                    src = kv.Value;
                    break;
                }
            }
        }

        // Prefer go-cq multi-forward (returns real message_id). forward_*_single_msg
        // returns data:null even when NTQQ silently drops the forward — not trustworthy.
        var idNode = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "node",
                ["data"] = new JsonObject { ["id"] = mid.ToString() },
            },
        };
        var (data, err) = await CallForwardMsgAsync(kind, peer, idNode);
        var newMid = NapCatApiClient.ReadLong(data?["message_id"]);

        if (newMid <= 0)
        {
            // Custom node with source text — still produces a real merged-forward card.
            var srcText = NapCatApiClient.ReadStr(src?["text"]);
            if (string.IsNullOrWhiteSpace(srcText)) srcText = "[转发消息]";
            var senderName = NapCatApiClient.ReadStr(src?["senderName"]);
            if (string.IsNullOrWhiteSpace(senderName))
                senderName = string.IsNullOrEmpty(_selfNickname) ? _selfUin.ToString() : _selfNickname;
            var senderUin = NapCatApiClient.ReadLong(src?["senderUin"]);
            if (senderUin <= 0) senderUin = _selfUin;
            var customNodes = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "node",
                    ["data"] = new JsonObject
                    {
                        ["user_id"] = senderUin.ToString(),
                        ["nickname"] = senderName,
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["data"] = new JsonObject { ["text"] = srcText },
                            },
                        },
                    },
                },
            };
            (data, err) = await CallForwardMsgAsync(kind, peer, customNodes);
            newMid = NapCatApiClient.ReadLong(data?["message_id"]);
        }

        if (newMid <= 0)
        {
            // Last resort: native single-msg forward (no message_id ack).
            var singleAction = kind == 'g' ? "forward_group_single_msg" : "forward_friend_single_msg";
            var singleParams = new JsonObject { ["message_id"] = mid };
            if (kind == 'g') singleParams["group_id"] = peer;
            else singleParams["user_id"] = peer;
            var (singleData, singleErr) = await _api.CallAsync(singleAction, singleParams);
            if (singleErr != null && err != null) return (null, err ?? singleErr);
            data = singleData ?? data;
            err = singleErr;
            newMid = NapCatApiClient.ReadLong(data?["message_id"]);
            if (newMid <= 0 && singleErr == null)
            {
                // API claims ok with empty body — treat as soft success but still surface a bubble.
                Console.WriteLine("[NapCat] forward single-msg returned no message_id (ok with null data)");
            }
            else if (newMid <= 0)
                return (null, err ?? "forward-failed");
        }

        var text = "[转发消息]";
        if (src != null)
        {
            var srcText = NapCatApiClient.ReadStr(src["text"]);
            if (!string.IsNullOrEmpty(srcText)) text = "[转发] " + srcText;
        }
        var wire = BuildOutgoingWire(
            conversationId,
            contentType: "Forward",
            text: text,
            imagePath: null,
            elements: new JsonArray { new JsonObject { ["Type"] = "Forward", ["Text"] = text, ["Url"] = newMid.ToString() } },
            messageId: newMid,
            forwardEntries: BuildForwardEntries(new[] { src }));
        return (wire, null);
    }

    public async Task<(JsonObject? data, string? error)> ForwardManyAsync(string conversationId, JsonArray messageIds)
    {
        if (!TryParseConv(conversationId, out var kind, out var peer))
            return (null, "invalid-conversation");
        var mids = new List<long>();
        var sources = new List<JsonObject?>();
        foreach (var value in messageIds ?? new JsonArray())
        {
            var id = NapCatApiClient.ReadStr(value);
            var mid = ExtractNapCatMessageId(id);
            if (mid <= 0) continue;
            mids.Add(mid);
            sources.Add(ResolveMessageWire(id, mid));
        }
        if (mids.Count == 0) return (null, "invalid-message-id");

        var nodes = new JsonArray();
        foreach (var mid in mids)
            nodes.Add(new JsonObject { ["type"] = "node", ["data"] = new JsonObject { ["id"] = mid.ToString() } });
        var (data, err) = await CallForwardMsgAsync(kind, peer, nodes);
        var newMid = NapCatApiClient.ReadLong(data?["message_id"]);
        if (newMid <= 0)
        {
            var customNodes = new JsonArray();
            foreach (var src in sources)
            {
                var senderName = NapCatApiClient.ReadStr(src?["senderName"]);
                if (string.IsNullOrEmpty(senderName)) senderName = _selfUin.ToString();
                var senderUin = NapCatApiClient.ReadLong(src?["senderUin"]);
                if (senderUin <= 0) senderUin = _selfUin;
                var srcText = NapCatApiClient.ReadStr(src?["text"]);
                if (string.IsNullOrEmpty(srcText)) srcText = "[消息]";
                customNodes.Add(new JsonObject
                {
                    ["type"] = "node",
                    ["data"] = new JsonObject
                    {
                        ["user_id"] = senderUin.ToString(),
                        ["nickname"] = senderName,
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = srcText } },
                        },
                    },
                });
            }
            (data, err) = await CallForwardMsgAsync(kind, peer, customNodes);
            newMid = NapCatApiClient.ReadLong(data?["message_id"]);
        }
        if (newMid <= 0) return (null, err ?? "forward-failed");
        var preview = "合并转发 " + mids.Count + " 条消息";
        var wire = BuildOutgoingWire(conversationId, "Forward", preview, null,
            new JsonArray { new JsonObject { ["Type"] = "Forward", ["Text"] = preview, ["Url"] = newMid.ToString() } },
            newMid, forwardEntries: BuildForwardEntries(sources));
        return (wire, null);
    }

    private JsonObject? ResolveMessageWire(string messageId, long mid)
    {
        if (_msgIndex.TryGetValue(messageId, out var exact)) return exact;
        foreach (var kv in _msgIndex)
            if (kv.Key.EndsWith(":" + mid, StringComparison.Ordinal)
                || NapCatApiClient.ReadLong(kv.Value["napcatMessageId"]) == mid)
                return kv.Value;
        return null;
    }

    private static JsonArray BuildForwardEntries(IEnumerable<JsonObject?> sources)
    {
        var entries = new JsonArray();
        foreach (var src in sources)
        {
            if (src == null) continue;
            entries.Add(new JsonObject
            {
                ["senderName"] = NapCatApiClient.ReadStr(src["senderName"]),
                ["text"] = string.IsNullOrEmpty(NapCatApiClient.ReadStr(src["text"])) ? "[消息]" : NapCatApiClient.ReadStr(src["text"]),
                ["imagePath"] = NapCatApiClient.ReadStr(src["imagePath"]),
            });
        }
        return entries;
    }

    private Task<(JsonNode? data, string? error)> CallForwardMsgAsync(char kind, long peer, JsonArray messages)
    {
        var action = kind == 'g' ? "send_group_forward_msg" : "send_private_forward_msg";
        var parameters = new JsonObject { ["messages"] = messages };
        if (kind == 'g') parameters["group_id"] = peer;
        else parameters["user_id"] = peer;
        return _api.CallAsync(action, parameters);
    }

    /// <summary>Friend/group recall notice → remove cached wire + push messageRecalled to Shell.</summary>
    private void HandleRecallNotice(JsonObject node)
    {
        var mid = NapCatApiClient.ReadLong(node["message_id"]);
        if (mid <= 0) return;
        var operatorId = NapCatApiClient.ReadLong(node["operator_id"] ?? node["user_id"]);
        var userId = NapCatApiClient.ReadLong(node["user_id"]);
        var groupId = NapCatApiClient.ReadLong(node["group_id"]);
        string convId;
        if (groupId > 0) convId = "g" + groupId;
        else
        {
            var peer = userId > 0 && userId != _selfUin ? userId : operatorId;
            if (peer <= 0 || peer == _selfUin)
                peer = userId > 0 ? userId : operatorId;
            convId = "f" + peer;
        }

        string? wireId = null;
        string? preview = null;
        string? senderName = null;
        long senderUin = 0;
        lock (_gate)
        {
            if (_messages.TryGetValue(convId, out var list))
            {
                var hit = list.FirstOrDefault(m =>
                    NapCatApiClient.ReadLong(m["napcatMessageId"]) == mid
                    || ((string?)m["id"])?.EndsWith(":" + mid, StringComparison.Ordinal) == true);
                if (hit != null)
                {
                    wireId = (string?)hit["id"];
                    preview = NapCatApiClient.ReadStr(hit["text"]);
                    senderName = NapCatApiClient.ReadStr(hit["senderName"]);
                    senderUin = NapCatApiClient.ReadLong(hit["senderUin"]);
                    list.Remove(hit);
                    if (!string.IsNullOrEmpty(wireId)) _msgIndex.TryRemove(wireId, out _);
                }
            }
            // Also scan index if conv guess was wrong (self-echo edge cases).
            if (wireId == null)
            {
                foreach (var kv in _msgIndex.ToList())
                {
                    if (NapCatApiClient.ReadLong(kv.Value["napcatMessageId"]) != mid) continue;
                    wireId = kv.Key;
                    preview = NapCatApiClient.ReadStr(kv.Value["text"]);
                    senderName = NapCatApiClient.ReadStr(kv.Value["senderName"]);
                    senderUin = NapCatApiClient.ReadLong(kv.Value["senderUin"]);
                    convId = NapCatApiClient.ReadStr(kv.Value["conversationId"]);
                    if (_messages.TryGetValue(convId, out var list2))
                        list2.RemoveAll(m => (string?)m["id"] == wireId);
                    _msgIndex.TryRemove(kv.Key, out _);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(wireId))
            wireId = convId + ":" + mid;

        Broadcast?.Invoke(new JsonObject
        {
            ["type"] = "messageRecalled",
            ["data"] = new JsonObject
            {
                ["conversationId"] = convId,
                ["messageId"] = wireId,
                ["napcatMessageId"] = mid,
                ["operatorUin"] = operatorId,
                ["senderUin"] = senderUin > 0 ? senderUin : userId,
                ["senderName"] = senderName ?? "",
                ["preview"] = preview ?? "",
                ["time"] = DateTimeOffset.UtcNow.ToString("o"),
            },
        }.ToJsonString());
    }
}
