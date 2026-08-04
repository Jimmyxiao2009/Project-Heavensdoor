using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
    public class EarlierMessagesResult
    {
        public System.Collections.Generic.IReadOnlyList<ChatMessage> Messages { get; set; }
        public bool HasMore { get; set; }
    }

    /// <summary>A member of a group conversation.</summary>
    public class MessageElement : ObservableObject
    {
        public string Type { get; set; } // "Text", "Image", "Record", "Video", "File", "Mention"
        public string Text { get; set; }
        public string Url { get; set; }
        public long Uin { get; set; }

        public bool IsText => Type == "Text" || Type == "Mention";
        public bool IsImage => Type == "Image" || Type == "Sticker";
        public bool IsVoice => Type == "Record" || Type == "Voice";
        public bool IsVideo => Type == "Video";
        public bool IsFile => Type == "File";
        
        public bool HasUrl => !string.IsNullOrEmpty(Url);
    }

    /// <summary>One preview row inside a merged-forward message.</summary>
    public sealed class ForwardEntry
    {
        public string SenderName { get; set; }
        public string Text { get; set; }
        public string ImagePath { get; set; }
        public bool HasImage => !string.IsNullOrEmpty(ImagePath);
        public string DisplayText => string.IsNullOrEmpty(SenderName)
            ? (Text ?? string.Empty)
            : SenderName + "：" + (Text ?? string.Empty);
    }

    /// <summary>A single message inside a conversation.</summary>
    public class ChatMessage : ObservableObject
    {
        public string Id { get; set; }
        public string ConversationId { get; set; }
        public string ConversationTitle { get; set; }
        public string ConversationAvatarPath { get; set; }
        public long SenderUin { get; set; }
        public string SenderName { get; set; }
        public string SenderAvatarPath { get; set; }
        public MessageDirection Direction { get; set; }

        private MessageContentType _contentType = MessageContentType.Text;
        public MessageContentType ContentType
        {
            get => _contentType;
            set
            {
                if (Set(ref _contentType, value))
                {
                    RaisePropertyChanged(nameof(IsText)); RaisePropertyChanged(nameof(IsImage));
                    RaisePropertyChanged(nameof(IsSticker)); RaisePropertyChanged(nameof(IsVoice));
                    RaisePropertyChanged(nameof(IsSystem));
                    RaisePropertyChanged(nameof(IsLinkCard)); RaisePropertyChanged(nameof(IsFile));
                    RaisePropertyChanged(nameof(IsLocation)); RaisePropertyChanged(nameof(IsVideo));
                    RaisePropertyChanged(nameof(IsForward));
                    RaisePropertyChanged(nameof(IsTextOnly)); RaisePropertyChanged(nameof(IsImageOnly));
                }
            }
        }

        private string _text;
        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        /// <summary>Peer (or self) withdrew this message; content may still be kept when 防撤回 is on.</summary>
        private bool _isRevoked;
        public bool IsRevoked
        {
            get => _isRevoked;
            set
            {
                if (Set(ref _isRevoked, value))
                    RaisePropertyChanged(nameof(RevokedBadgeText));
            }
        }
        public string RevokedBadgeText => _isRevoked ? "已撤回 · 本地保留" : string.Empty;

        public System.Collections.ObjectModel.ObservableCollection<MessageElement> Elements { get; set; } = new System.Collections.ObjectModel.ObservableCollection<MessageElement>();
        public System.Collections.ObjectModel.ObservableCollection<ForwardEntry> ForwardEntries { get; } = new System.Collections.ObjectModel.ObservableCollection<ForwardEntry>();
        
        public bool HasElements => Elements != null && Elements.Count > 0;
        public bool HasNoElements => !HasElements;
        public bool HasForwardEntries => ForwardEntries.Count > 0;
        public string ForwardPreview => ForwardEntries.Count == 0
            ? (Text ?? "[转发消息]")
            : string.Join("\n", ForwardEntries.Take(3).Select(x => x.DisplayText));

        /// <summary>
        /// True when the bubble should use the multi-part layout (text+image, multi-image,
        /// text+file, …). Pure single-image / pure text keep their dedicated templates so
        /// outgoing thumbnails still bind to <see cref="ImagePath"/> when CDN URL is empty.
        /// </summary>
        public bool UseMixedLayout
        {
            get
            {
                if (Elements == null || Elements.Count == 0) return false;
                int imageCount = 0;
                bool hasText = false;
                bool hasOther = false;
                foreach (var e in Elements)
                {
                    if (e == null) continue;
                    if (e.IsImage) imageCount++;
                    else if (e.IsText)
                    {
                        if (!string.IsNullOrEmpty(e.Text)) hasText = true;
                    }
                    else hasOther = true;
                }
                if (imageCount > 1) return true;
                if (hasText && imageCount > 0) return true;
                if (hasText && hasOther) return true;
                if (hasOther && imageCount > 0) return true;
                return false;
            }
        }

        // Pure text often still has a single Text element from the wire "elements" array.
        // Do NOT require HasNoElements — that hid every caption-less text bubble after
        // RealServer started always populating elements.
        public bool IsTextOnly => IsText && !UseMixedLayout;
        public bool IsImageOnly => IsImage && !UseMixedLayout;

        private string _imagePath;
        /// <summary>Local path, ms-app* URI, or remote CDN URL for image/sticker messages.
        /// Notifies so a late resolve (getMediaUrl) can refresh the bubble thumbnail.</summary>
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (Set(ref _imagePath, value))
                    RaisePropertyChanged(nameof(HasImagePath));
            }
        }
        public bool HasImagePath => !string.IsNullOrEmpty(_imagePath);

        private string _audioPath;
        public string AudioPath
        {
            get => _audioPath;
            set => Set(ref _audioPath, value);
        }
        public int VoiceSeconds { get; set; }
        public DateTimeOffset Time { get; set; }

        // ---- reply quote ----
        public string ReplyToSender { get; set; }
        public string ReplyToText { get; set; }
        public bool HasReply => !string.IsNullOrEmpty(ReplyToSender);
        public string ReplyPreview => HasReply ? ReplyToSender + "：" + ReplyToText : string.Empty;

        // ---- link card ----
        public string LinkTitle { get; set; }
        public string LinkSource { get; set; }
        public string LinkThumb { get; set; }

        // ---- file ----
        public string FileName { get; set; }
        public string FileSize { get; set; }
        /// <summary>Group file id for GroupFSDownload (not the chat message id).</summary>
        public string FileId { get; set; }
        /// <summary>Local ms-appdata path cached when we ourselves sent the file, so the
        /// outgoing card can reopen without a remote download URL.</summary>
        public string LocalFilePath { get; set; }
        public string FileActionHint =>
            !string.IsNullOrEmpty(LocalFilePath) ? "点击打开本地文件"
            : !string.IsNullOrEmpty(FileId) && !FileId.StartsWith("friend-file:", StringComparison.Ordinal)
                ? "点击下载"
                : "点击保存/查看";

        // ---- location ----
        public string PlaceName { get; set; }
        public string PlaceAddress { get; set; }
        public string PlaceThumb { get; set; }
        public double PlaceLatitude { get; set; }
        public double PlaceLongitude { get; set; }

        // ---- reactions ----
        public ObservableCollection<string> Reactions { get; } = new ObservableCollection<string>();
        public bool HasReactions => Reactions.Count > 0;
        public string ReactionText => string.Join("  ", Reactions);

        public void ToggleReaction(string emoji)
        {
            if (Reactions.Contains(emoji)) Reactions.Remove(emoji);
            else Reactions.Add(emoji);
            RaisePropertyChanged(nameof(HasReactions));
            RaisePropertyChanged(nameof(ReactionText));
        }

        // ---- read receipt ----
        private bool _isRead;
        public bool IsRead { get => _isRead; set { Set(ref _isRead, value); RaisePropertyChanged(nameof(ReadText)); } }
        public string ReadText => _isRead ? "已读" : "未读";

        private bool _showReadState;
        public bool ShowReadState { get => _showReadState; set => Set(ref _showReadState, value); }

        // ---- content-type flags ----
        public bool IsText => _contentType == MessageContentType.Text;
        public bool IsImage => _contentType == MessageContentType.Image;
        public bool IsSticker => _contentType == MessageContentType.Sticker;
        public bool IsVoice => _contentType == MessageContentType.Voice;
        public bool IsSystem => _contentType == MessageContentType.System;
        public bool IsLinkCard => _contentType == MessageContentType.LinkCard;
        public bool IsFile => _contentType == MessageContentType.FileMsg;
        public bool IsLocation => _contentType == MessageContentType.Location;
        public bool IsVideo => _contentType == MessageContentType.Video;
        public bool IsForward => _contentType == MessageContentType.Forward;

        public string VoiceText => VoiceSeconds + "″";
        public double VoiceWidth => System.Math.Min(60 + VoiceSeconds * 8, 200);

        /// <summary>Video duration formatted mm:ss (VoiceSeconds carries the video's length
        /// too -- server contract: video messages set voiceSeconds to the clip's duration).</summary>
        public string VideoText
        {
            get
            {
                var total = System.Math.Max(0, VoiceSeconds);
                var minutes = total / 60;
                var seconds = total % 60;
                return minutes.ToString("D2") + ":" + seconds.ToString("D2");
            }
        }

        private bool _showTimeDivider;
        public bool ShowTimeDivider { get => _showTimeDivider; set { Set(ref _showTimeDivider, value); RaisePropertyChanged(nameof(DividerText)); } }
        public string DividerText => FullTimeText(Time);

        internal static string FullTimeText(DateTimeOffset t)
        {
            var now = DateTimeOffset.Now;
            string day;
            if (t.Date == now.Date) day = string.Empty;
            else if (t.Date == now.Date.AddDays(-1)) day = "昨天 ";
            else if ((now.Date - t.Date).TotalDays < 7) day = t.ToString("dddd") + " ";
            else day = t.ToString("MM/dd") + " ";
            return day + t.ToString("HH:mm");
        }

        private MessageState _state = MessageState.Sent;
        public MessageState State { get => _state; set { Set(ref _state, value); RaisePropertyChanged(nameof(IsFailed)); RaisePropertyChanged(nameof(IsSending)); } }

        public bool IsOutgoing => Direction == MessageDirection.Outgoing;
        public bool IsIncoming => Direction == MessageDirection.Incoming;
        public bool IsFailed => _state == MessageState.Failed;
        public bool IsSending => _state == MessageState.Sending;
        public string TimeText => Time.ToString("HH:mm");
    }
}
