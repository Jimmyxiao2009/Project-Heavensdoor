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
    public partial class RemoteChatService
    {
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
