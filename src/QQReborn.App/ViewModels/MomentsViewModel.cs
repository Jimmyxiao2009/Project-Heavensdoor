using System.Collections.ObjectModel;
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

        public MomentsViewModel(IMomentsService moments)
        {
            _moments = moments;
            LikeCommand = new RelayCommand(p => { var _ = ToggleLikeAsync(p as Moment); });
        }

        public ObservableCollection<Moment> Feed { get; } = new ObservableCollection<Moment>();

        /// <summary>Toggles a like; bound from the footer ♥ button (CommandParameter = the Moment).</summary>
        public RelayCommand LikeCommand { get; }

        private bool _isLoaded;

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            var feed = await _moments.GetFeedAsync();
            Feed.Clear();
            foreach (var m in feed) Feed.Add(m);
        }

        public async Task ToggleLikeAsync(Moment m)
        {
            if (m == null) return;
            await _moments.ToggleLikeAsync(m);
        }

        public async Task AddCommentAsync(Moment m, string text)
        {
            if (m == null || string.IsNullOrWhiteSpace(text)) return;
            await _moments.AddCommentAsync(m, text);
        }

        /// <summary>"发表动态": prepends a local moment with optional images / video.</summary>
        public void PublishLocal(string text, System.Collections.Generic.IList<string> images = null, string video = null)
        {
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
