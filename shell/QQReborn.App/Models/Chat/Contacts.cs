using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    public class GroupMember
    {
        public long Uin { get; set; }
        public string Name { get; set; }
        public string AvatarPath { get; set; }
        public string Role { get; set; }   // 群主 / 管理员 / "" (member)

        public bool HasRole => !string.IsNullOrEmpty(Role);
        public bool IsOwner => Role == "群主";
        /// <summary>群主或管理员（可执行多数群管操作）。</summary>
        public bool IsAdmin => Role == "群主" || Role == "管理员";
    }

    /// <summary>Normalized group notice from NapCat <c>_get_group_notice</c>.</summary>
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

        /// <summary>ISO-8601 last-read watermark from gateway (optional).</summary>
        public string LastReadAt { get; set; }

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
}
