using System.Collections.Generic;
using System.Collections.ObjectModel;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
    /// <summary>A single comment under a moment (动态评论).</summary>
    public class MomentComment
    {
        public string Author { get; set; }
        public string Text { get; set; }
    }

    /// <summary>One entry in the friend feed (QQ空间 / 好友动态).</summary>
    public class Moment : ObservableObject
    {
        public string Id { get; set; }
        public string AuthorName { get; set; }
        public string AuthorAvatarPath { get; set; }
        public string Text { get; set; }
        public string TimeText { get; set; }
        /// <summary>Absolute ISO timestamp when available (for sorting).</summary>
        public string Time { get; set; }

        /// <summary>0..3 ms-appx asset paths used as fake photos.</summary>
        public List<string> ImagePaths { get; set; } = new List<string>();

        /// <summary>Optional video (ms-appdata/ms-appx path); rendered with a MediaElement.</summary>
        public string VideoPath { get; set; }
        public bool HasVideo => !string.IsNullOrEmpty(VideoPath);

        public bool HasImages => ImagePaths != null && ImagePaths.Count > 0;
        public bool HasText => !string.IsNullOrEmpty(Text);

        /// <summary>True when exactly one image -> show one big photo.</summary>
        public bool IsSingleImage => ImagePaths != null && ImagePaths.Count == 1;

        /// <summary>True when more than one image -> show the multi-up grid.</summary>
        public bool IsMultiImage => ImagePaths != null && ImagePaths.Count > 1;

        public string SingleImagePath => (ImagePaths != null && ImagePaths.Count > 0) ? ImagePaths[0] : null;

        private int _likeCount;
        public int LikeCount
        {
            get => _likeCount;
            set { if (Set(ref _likeCount, value)) RaisePropertyChanged(nameof(LikeText)); }
        }

        private bool _isLiked;
        public bool IsLiked { get => _isLiked; set => Set(ref _isLiked, value); }

        public string LikeText => _likeCount.ToString();

        private string _likersText = string.Empty;
        public string LikersText
        {
            get => _likersText;
            set
            {
                if (Set(ref _likersText, value ?? string.Empty))
                    RaisePropertyChanged(nameof(HasLikers));
            }
        }
        public bool HasLikers => !string.IsNullOrEmpty(_likersText);

        /// <summary>Observable so newly added comments appear live.</summary>
        public ObservableCollection<MomentComment> Comments { get; } = new ObservableCollection<MomentComment>();

        public bool HasComments => Comments.Count > 0;
        public string CommentCountText => Comments.Count.ToString();

        /// <summary>Call after mutating Comments to refresh dependent bindings.</summary>
        public void RaiseCommentsChanged()
        {
            RaisePropertyChanged(nameof(HasComments));
            RaisePropertyChanged(nameof(CommentCountText));
        }
    }
}
