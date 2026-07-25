using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Moments backed by RealServer's webhook-ingested 空间 feed (POST /webhook/space).
    /// Like state is synchronized through RealServer; comments remain local until a
    /// corresponding space write operation is available.
    /// </summary>
    public sealed class RemoteMomentsService : IMomentsService
    {
        private readonly RemoteChatService _remote;

        public RemoteMomentsService(RemoteChatService remote)
        {
            _remote = remote;
        }

        public Task<IReadOnlyList<Moment>> GetFeedAsync()
            => _remote.GetSpaceFeedAsync(forceRefresh: false);

        public Task<IReadOnlyList<Moment>> RefreshFeedAsync()
            => _remote.GetSpaceFeedAsync(forceRefresh: true);

        public async Task ToggleLikeAsync(Moment m)
        {
            if (m == null) return;

            var next = !m.IsLiked;
            if (!await _remote.SetSpaceLikeAsync(m.Id, next)) return;

            if (!next)
            {
                if (m.LikeCount > 0) m.LikeCount--;
            }
            else
            {
                m.LikeCount++;
            }
            m.IsLiked = next;
        }

        public Task AddCommentAsync(Moment m, string text)
        {
            if (m == null || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
            m.Comments.Add(new MomentComment { Author = "我", Text = text.Trim() });
            m.RaiseCommentsChanged();
            return Task.CompletedTask;
        }

        public Task<bool> GetEarlierFeedAsync()
            => _remote.GetEarlierSpaceFeedAsync();
    }
}
