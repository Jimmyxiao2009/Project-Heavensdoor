using System;
using System.Collections.ObjectModel;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
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
        Location
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

    /// <summary>A single message inside a conversation.</summary>
    public class ChatMessage : ObservableObject
    {
        public string Id { get; set; }
        public string ConversationId { get; set; }
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
                    RaisePropertyChanged(nameof(IsLocation));
                }
            }
        }

        public string Text { get; set; }
        public string ImagePath { get; set; }
        public string AudioPath { get; set; }
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

        public string VoiceText => VoiceSeconds + "″";
        public double VoiceWidth => System.Math.Min(60 + VoiceSeconds * 8, 200);

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
