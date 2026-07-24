using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
    /// <summary>One entry in the conversation image gallery (full-screen swipe viewer).</summary>
    public sealed class ImageGalleryItem
    {
        public string MessageId { get; set; }
        /// <summary>Local path or CDN URL; may be empty until resolved via getMediaUrl.</summary>
        public string Path { get; set; }
    }

    /// <summary>Navigation parameter for <see cref="Views.ImageViewerPage"/>.</summary>
    public sealed class ImageGalleryArgs
    {
        public IList<ImageGalleryItem> Items { get; set; }
        public int Index { get; set; }
    }

    public enum ConversationKind
    {
        Friend,
        Group
    }

    public enum MessageDirection
    {
        Incoming,
        Outgoing
    }

    public enum MessageContentType
    {
        Text,
        Image,
        Sticker,
        Voice,
        System,
        LinkCard,
        FileMsg,
        Location,
        Video
    }

    public enum MessageState
    {
        Sending,
        Sent,
        Failed
    }

    public enum OnlineStatus
    {
        Online,
        Away,
        Busy,
        DoNotDisturb,
        Invisible
    }

    /// <summary>Identity of the logged-in user.</summary>
    public class SelfProfile : ObservableObject
    {
        public long Uin { get; set; }
        public string Nickname { get; set; }
        public string AvatarPath { get; set; }
        public string Signature { get; set; }
        public int Level { get; set; }

        private OnlineStatus _status = OnlineStatus.Online;
        public OnlineStatus Status
        {
            get => _status;
            set { if (Set(ref _status, value)) { RaisePropertyChanged(nameof(StatusText)); RaisePropertyChanged(nameof(StatusColorHex)); } }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case OnlineStatus.Away: return "离开";
                    case OnlineStatus.Busy: return "忙碌";
                    case OnlineStatus.DoNotDisturb: return "请勿打扰";
                    case OnlineStatus.Invisible: return "隐身";
                    default: return "在线";
                }
            }
        }

        /// <summary>Hex color for the status dot (views can make a brush from it).</summary>
        public string StatusColorHex
        {
            get
            {
                switch (_status)
                {
                    case OnlineStatus.Away: return "#FFF5A623";
                    case OnlineStatus.Busy: return "#FFFA5151";
                    case OnlineStatus.DoNotDisturb: return "#FFFA5151";
                    case OnlineStatus.Invisible: return "#FF8A8A8A";
                    default: return "#FF12B7F5";
                }
            }
        }
    }

    /// <summary>A friend / contact entry.</summary>
    public class Contact
    {
        public long Uin { get; set; }
        public string Name { get; set; }
        public string AvatarPath { get; set; }
        public string Signature { get; set; }
        public bool Online { get; set; }
        public string Remark { get; set; }
        public string Gender { get; set; }   // 男 / 女 / ""
        public string Location { get; set; }
        public int Age { get; set; }

        public string DisplayName => string.IsNullOrEmpty(Remark) ? Name : Remark;
    }

    /// <summary>Full profile detail for a user, fetched on demand (e.g. contact-detail page).</summary>
    public class UserProfile
    {
        public long Uin { get; set; }
        public string Nickname { get; set; }
        public string Signature { get; set; }
        public int Level { get; set; }
        public string Gender { get; set; }   // "male" / "female" / null
        public int Age { get; set; }
        public string Country { get; set; }
        public string City { get; set; }
    }

    /// <summary>Result of a page of older messages fetched via GetEarlierMessagesAsync.</summary>
    public class EarlierMessagesResult
    {
        public System.Collections.Generic.IReadOnlyList<ChatMessage> Messages { get; set; }
        public bool HasMore { get; set; }
    }

    /// <summary>A member of a group conversation.</summary>
    public class GroupMember
    {
        public long Uin { get; set; }
        public string Name { get; set; }
        public string AvatarPath { get; set; }
        public string Role { get; set; }   // 群主 / 管理员 / "" (member)

        public bool HasRole => !string.IsNullOrEmpty(Role);
        public bool IsOwner => Role == "群主";
    }

    /// <summary>A pending friend request.</summary>
    public class FriendRequest : ObservableObject
    {
        public long Uin { get; set; }
        public string Name { get; set; }
        public string AvatarPath { get; set; }
        public string Message { get; set; }

        private bool _handled;
        public bool Handled { get => _handled; set { Set(ref _handled, value); RaisePropertyChanged(nameof(NotHandled)); } }
        public bool NotHandled => !_handled;
    }

    /// <summary>A row in the conversation (会话) list.</summary>
    public class ChatConversation : ObservableObject
    {
        public string Id { get; set; }
        public ConversationKind Kind { get; set; }

        public bool IsGroup => Kind == ConversationKind.Group;

        private string _title;
        public string Title { get => _title; set => Set(ref _title, value); }

        public string AvatarPath { get; set; }

        /// <summary>Group announcement text (Group conversations only; null for Friend). Plain
        /// property -- no change notification needed, matches the field's fetch-once usage.</summary>
        public string Announcement { get; set; }

        private string _preview;
        public string Preview { get => _preview; set => Set(ref _preview, value); }

        private DateTimeOffset _lastTime;
        public DateTimeOffset LastTime { get => _lastTime; set { Set(ref _lastTime, value); RaisePropertyChanged(nameof(LastTimeText)); } }

        private int _unread;
        public int Unread { get => _unread; set { Set(ref _unread, value); RaisePropertyChanged(nameof(HasUnread)); RaisePropertyChanged(nameof(UnreadText)); } }

        private bool _isPinned;
        public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

        private bool _isMuted;
        public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }

        private bool _atMe;
        public bool AtMe { get => _atMe; set { Set(ref _atMe, value); RaisePropertyChanged(nameof(PreviewDisplay)); } }

        public bool HasUnread => _unread > 0;
        public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();

        /// <summary>Preview prefixed with a [有人@我] badge when mentioned.</summary>
        public string PreviewDisplay => _atMe ? "[有人@我] " + _preview : _preview;

        public string LastTimeText => FormatTime(_lastTime);

        internal static string FormatTime(DateTimeOffset t)
        {
            var now = DateTimeOffset.Now;
            if (t.Date == now.Date) return t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "昨天";
            if ((now.Date - t.Date).TotalDays < 7) return t.ToString("dddd");
            return t.ToString("yyyy/MM/dd");
        }
    }

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
        
        public bool HasElements => Elements != null && Elements.Count > 0;
        public bool HasNoElements => !HasElements;

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
