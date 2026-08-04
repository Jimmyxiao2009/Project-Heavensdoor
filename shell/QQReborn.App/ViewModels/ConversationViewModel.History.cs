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
        public async Task LoadEarlierAsync()
        {
            if (!CanLoadEarlier || _loadingEarlier) return;
            var loadGeneration = _loadGeneration;
            var conversationId = ConversationId;
            _loadingEarlier = true;
            try
            {
                if (_chat is IGatewayService remote) await LoadEarlierRemoteAsync(remote, conversationId, loadGeneration);
                else await LoadEarlierMockAsync();
            }
            finally
            {
                if (IsCurrentLoad(loadGeneration, conversationId)) _loadingEarlier = false;
            }
        }

        /// <summary>
        /// Prepend a small batch of fabricated, older messages to the TOP of the list,
        /// simulating "查看更多消息" history paging. Limited to MaxEarlierLoads batches.
        /// </summary>
        private Task LoadEarlierMockAsync()
        {
            // Anchor before the oldest message currently shown so new ones land earlier.
            var oldest = Messages.Count > 0 ? Messages[0].Time : DateTimeOffset.Now;
            var baseTime = oldest.AddMinutes(-(10 * (_earlierLoadCount + 1)) - 5);

            const string selfAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
            const string peerAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
            var peerName = IsGroup ? "群友" : (string.IsNullOrEmpty(Title) ? "对方" : Title);

            // Canned chat lines (alternating peer / self), oldest first.
            var lines = new[] { "在干嘛呢？", "刚下班，你呢", "我也刚到家", "晚点一起开黑？", "好啊，叫上他们" };

            var batch = new System.Collections.Generic.List<ChatMessage>();
            for (int i = 0; i < lines.Length; i++)
            {
                var incoming = (i % 2) == 0; // start with peer
                batch.Add(new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = ConversationId,
                    Direction = incoming ? MessageDirection.Incoming : MessageDirection.Outgoing,
                    ContentType = MessageContentType.Text,
                    Text = lines[i],
                    SenderName = incoming ? peerName : "我",
                    SenderAvatarPath = incoming ? peerAvatar : selfAvatar,
                    Time = baseTime.AddSeconds(i * 30),
                    State = MessageState.Sent
                });
            }

            // Insert the batch at the top, preserving its internal order.
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                Messages.Insert(0, batch[i]);
            }

            // Time dividers depend on neighbours, so recompute across the whole list.
            RecomputeDividers();
            UpdateReadState();

            _earlierLoadCount++;
            if (_earlierLoadCount >= MaxEarlierLoads) CanLoadEarlier = false;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Real backfill against a remote backend: anchors on the oldest REAL message
        /// currently on screen (skipping system notices -- 撤回/戳一戳 lines aren't part of
        /// the server's history and have no id the server would recognize), fetches one page
        /// older, de-dupes against what's already shown, and prepends it.
        /// </summary>
        private async Task LoadEarlierRemoteAsync(IGatewayService remote, string conversationId, int loadGeneration)
        {
            // Prefer the oldest real message as the page-before anchor. If the chat is empty
            // (or only system notices), pass null so the server pulls the newest cloud page
            // (friend roam from "now" / group FetchGroupExtra + recent sequences).
            var anchor = FirstRealMessage();
            var beforeId = anchor != null ? anchor.Id : null;

            EarlierMessagesResult result;
            try
            {
                result = await remote.GetEarlierMessagesAsync(conversationId, beforeId, 20);
            }
            catch (Exception ex)
            {
                if (!IsCurrentLoad(loadGeneration, conversationId)) return;
                // Keep the strip up so the user can retry (transient network blip); nothing
                // in Messages changed, so there's nothing to roll back.
                var detail = string.IsNullOrEmpty(ex.Message) ? "请稍后重试" : ex.Message;
                AppendSystem("加载历史消息失败（" + detail + "）");
                return;
            }

            if (!IsCurrentLoad(loadGeneration, conversationId)) return;

            var older = result != null ? result.Messages : null;
            if (older == null || older.Count == 0)
            {
                // Empty page: either no cloud history, or we've hit the beginning.
                if (!HasAnyRealMessage())
                    AppendSystem("暂无更多云端历史消息");
                CanLoadEarlier = false;
                return;
            }

            // Server contract: Messages is oldest-first. De-dupe against everything already
            // shown (not just the anchor) -- a retry after a partial failure, or a page whose
            // window overlaps what's already on screen, could otherwise double-insert.
            var existingIds = new System.Collections.Generic.HashSet<string>();
            foreach (var existing in Messages)
                if (!string.IsNullOrEmpty(existing.Id)) existingIds.Add(existing.Id);

            var toInsert = new System.Collections.Generic.List<ChatMessage>();
            foreach (var m in older)
            {
                if (!string.IsNullOrEmpty(m.Id) && existingIds.Contains(m.Id)) continue;
                toInsert.Add(m);
            }

            if (toInsert.Count > 0)
            {
                // Insert at the top, preserving oldest-first order (batch[0] is oldest).
                // When the pull was unanchored and Messages was empty, this just fills the
                // list; when paging older, they land above the previous first item.
                for (int i = toInsert.Count - 1; i >= 0; i--)
                    Messages.Insert(0, toInsert[i]);

                // Dividers depend on neighbours; the prepended batch changes what's above the
                // messages that used to be at the top, so recompute across the whole list
                // rather than trying to patch just the seam.
                RecomputeDividers();
                UpdateReadState();
            }

            // Keep paging while the server says there is more OR we actually inserted new
            // rows (a full page of dups still sets HasMore so the next tap re-anchors older).
            // Only collapse when the page is empty or HasMore is false.
            if (toInsert.Count == 0 && !result.HasMore)
                CanLoadEarlier = false;
            else
                CanLoadEarlier = result.HasMore || toInsert.Count > 0;
        }

        /// <summary>Oldest message currently shown that represents real conversation content
        /// -- i.e. not a local system notice (撤回/戳一戳/连接失败 lines), which never came
        /// from the server and has no id the server's history endpoint would recognize.</summary>
        private ChatMessage FirstRealMessage()
        {
            foreach (var m in Messages)
                if (!m.IsSystem) return m;
            return null;
        }

        private bool HasAnyRealMessage() => FirstRealMessage() != null;

        /// <summary>Recompute the time-divider flags for every message in order.</summary>
        private void RecomputeDividers()
        {
            ChatMessage prev = null;
            foreach (var m in Messages)
            {
                ApplyDivider(prev, m);
                prev = m;
            }
        }

        private async Task LoadStickersAsync()
        {
            try
            {
                if (_chat is IGatewayService remote)
                {
                    try
                    {
                        var remoteStickers = await remote.GetFavoriteStickersAsync();
                        foreach (var sticker in remoteStickers)
                            if (!string.IsNullOrEmpty(sticker) && !Stickers.Contains(sticker))
                                Stickers.Add(sticker);
                    }
                    catch { }
                }
                var assets = await Package.Current.InstalledLocation.GetFolderAsync("Assets");
                var folder = await assets.GetFolderAsync("Emoticons");
                var files = await folder.GetFilesAsync();
                foreach (var f in files)
                {
                    Stickers.Add("ms-appx:///Assets/Emoticons/" + f.Name);
                }
            }
            catch
            {
                // The bundled set remains usable when NapCat is offline.
            }
        }

        public void InsertEmoji(string emoji) => Draft = (_draft ?? string.Empty) + emoji;
    }
}
