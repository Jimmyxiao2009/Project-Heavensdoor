using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    public partial class RemoteChatService
    {
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

    }
}
