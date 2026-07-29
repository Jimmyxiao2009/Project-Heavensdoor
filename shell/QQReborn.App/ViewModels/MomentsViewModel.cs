using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public class MomentsViewModel : ObservableObject
    {
        private readonly IMomentsService _moments;
        private int _localSeed;
        private bool _isRefreshing;

        /// <summary>
        /// The real backend is fed by RealServer's space webhook. The view model keeps the
        /// native Metro presentation independent from how the feed was collected.
        /// </summary>
        public MomentsViewModel(IMomentsService moments)
        {
            _moments = moments;
            LikeCommand = new RelayCommand(p => { var _ = ToggleLikeAsync(p as Moment); });
        }

        public ObservableCollection<Moment> Feed { get; } = new ObservableCollection<Moment>();

        /// <summary>True when there is no feed backend -- the view binds its empty state to this.</summary>
        public bool IsUnsupported => _moments == null;

        /// <summary>Inverse of <see cref="IsUnsupported"/>, for binding the compose row / feed list
        /// visibility without needing a separate inverse-bool XAML converter.</summary>
        public bool IsSupported => _moments != null;

        /// <summary>Publishing is intentionally local-only until a real QZone write API is wired.</summary>
        public bool IsPublishSupported => _moments is MockMomentsService;

        /// <summary>Supported backend but no posts yet (e.g. waiting for /webhook/space).</summary>
        public bool IsEmptyFeed => _moments != null && Feed.Count == 0;

        /// <summary>True while the native backend reports more history pages available.</summary>
        private bool _hasMore = true;
        public bool HasMore
        {
            get => _hasMore;
            set { if (Set(ref _hasMore, value)) RaisePropertyChanged(nameof(HasMore)); }
        }

        /// <summary>Toggles a like; bound from the footer ♥ button (CommandParameter = the Moment).</summary>
        public RelayCommand LikeCommand { get; }

        private bool _isLoaded;

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            await RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (_moments == null || _isRefreshing) return;
            _isRefreshing = true;

            try
            {
                var feed = await _moments.RefreshFeedAsync();
                MergeFeed(feed);
                RaisePropertyChanged(nameof(IsEmptyFeed));
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        /// <summary>
        /// Merge a fresh feed into the bound collection without Clear()+rebuild.
        /// Preserves ListView scroll position and keeps newest-first ordering.
        /// </summary>
        private void MergeFeed(System.Collections.Generic.IReadOnlyList<Moment> fresh)
        {
            if (fresh == null) return;

            // Index existing by id for O(1) updates.
            var byId = new System.Collections.Generic.Dictionary<string, Moment>();
            foreach (var m in Feed)
            {
                if (m != null && !string.IsNullOrEmpty(m.Id) && !byId.ContainsKey(m.Id))
                    byId[m.Id] = m;
            }

            // A cold list has no visible position to preserve. Adding it in final order avoids
            // a second pass that moves almost every item and blocks the UI on larger feeds.
            if (Feed.Count == 0)
            {
                foreach (var f in fresh
                    .Where(m => m != null && !string.IsNullOrEmpty(m.Id))
                    .OrderByDescending(ParseMomentTime)
                    .ThenByDescending(m => m.Id))
                {
                    Feed.Add(f);
                    byId[f.Id] = f;
                }
                return;
            }

            var toReposition = new System.Collections.Generic.List<Moment>();
            foreach (var f in fresh)
            {
                if (f == null || string.IsNullOrEmpty(f.Id)) continue;
                Moment existing;
                if (byId.TryGetValue(f.Id, out existing) && existing != null)
                {
                    // Update fields in place so item containers are not recreated.
                    var sortChanged = ParseMomentTime(existing) != ParseMomentTime(f);
                    existing.AuthorName = f.AuthorName;
                    existing.AuthorAvatarPath = f.AuthorAvatarPath;
                    existing.Text = f.Text;
                    existing.TimeText = f.TimeText;
                    existing.Time = f.Time;
                    existing.VideoPath = f.VideoPath;
                    existing.LikeCount = f.LikeCount;
                    existing.IsLiked = f.IsLiked;
                    existing.LikersText = f.LikersText;
                    if (!SameImagePaths(existing.ImagePaths, f.ImagePaths))
                    {
                        existing.ImagePaths.Clear();
                        if (f.ImagePaths != null)
                            foreach (var img in f.ImagePaths) existing.ImagePaths.Add(img);
                    }
                    if (!SameComments(existing.Comments, f.Comments))
                    {
                        existing.Comments.Clear();
                        if (f.Comments != null)
                            foreach (var c in f.Comments) existing.Comments.Add(c);
                        existing.RaiseCommentsChanged();
                    }
                    if (sortChanged) toReposition.Add(existing);
                }
                else
                {
                    Feed.Add(f);
                    byId[f.Id] = f;
                    toReposition.Add(f);
                }
            }

            // Most refreshes only update likes/comments. Only posts whose timestamp changed
            // (or newly arrived posts) need a Move notification and a ListView relayout.
            foreach (var moment in toReposition.Distinct())
                MoveMomentToSortedPosition(moment);
        }

        private void MoveMomentToSortedPosition(Moment moment)
        {
            if (moment == null) return;
            var current = Feed.IndexOf(moment);
            if (current < 0) return;

            var target = 0;
            foreach (var other in Feed)
            {
                if (!object.ReferenceEquals(other, moment) && CompareMoments(other, moment) < 0)
                    target++;
            }
            if (current != target) Feed.Move(current, target);
        }

        private static int CompareMoments(Moment left, Moment right)
        {
            if (object.ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            var time = ParseMomentTime(right).CompareTo(ParseMomentTime(left));
            return time != 0 ? time : string.Compare(right.Id, left.Id, StringComparison.Ordinal);
        }

        private static bool SameImagePaths(System.Collections.Generic.IList<string> left,
            System.Collections.Generic.IList<string> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return left == null && right == null;
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool SameComments(System.Collections.Generic.IList<MomentComment> left,
            System.Collections.Generic.IList<MomentComment> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return left == null && right == null;
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i]?.Author, right[i]?.Author, StringComparison.Ordinal)
                    || !string.Equals(left[i]?.Text, right[i]?.Text, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static System.DateTimeOffset ParseMomentTime(Moment m)
        {
            if (m == null) return System.DateTimeOffset.MinValue;
            System.DateTimeOffset dto;
            if (!string.IsNullOrEmpty(m.Time) && System.DateTimeOffset.TryParse(m.Time, out dto))
                return dto;
            if (!string.IsNullOrEmpty(m.TimeText) && System.DateTimeOffset.TryParse(m.TimeText, out dto))
                return dto;
            return System.DateTimeOffset.MinValue;
        }

        /// <summary>Load the next page of older 空间动态. Returns hasMore.</summary>
        public async Task<bool> LoadMoreAsync()
        {
            if (_moments == null || _isRefreshing) return HasMore;
            _isRefreshing = true;
            try
            {
                // Server fetch merges into its cache and may push spaceFeedUpdated.
                // That push can arrive while _isRefreshing is true and would be dropped
                // by RefreshAsync — so always re-pull + MergeFeed here after the page load.
                HasMore = await _moments.GetEarlierFeedAsync();
                var feed = await _moments.GetFeedAsync();
                MergeFeed(feed);
                RaisePropertyChanged(nameof(IsEmptyFeed));
                return HasMore;
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        public async Task ToggleLikeAsync(Moment m)
        {
            if (m == null || _moments == null) return;
            await _moments.ToggleLikeAsync(m);
        }

        public async Task AddCommentAsync(Moment m, string text)
        {
            if (m == null || string.IsNullOrWhiteSpace(text) || _moments == null) return;
            await _moments.AddCommentAsync(m, text);
        }

        /// <summary>"发表动态": prepends a local moment with optional images / video.</summary>
        public void PublishLocal(string text, System.Collections.Generic.IList<string> images = null, string video = null)
        {
            if (_moments == null) return;
            bool hasText = !string.IsNullOrWhiteSpace(text);
            bool hasImages = images != null && images.Count > 0;
            bool hasVideo = !string.IsNullOrEmpty(video);
            if (!hasText && !hasImages && !hasVideo) return;

            var m = new Moment
            {
                Id = "local" + (++_localSeed),
                AuthorName = "Jimmy",
                AuthorAvatarPath = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png",
                Text = hasText ? text.Trim() : string.Empty,
                TimeText = "刚刚",
                LikeCount = 0,
                IsLiked = false,
                VideoPath = video
            };
            if (hasImages)
            {
                foreach (var p in images) m.ImagePaths.Add(p);
            }
            Feed.Insert(0, m);
        }
    }
}
