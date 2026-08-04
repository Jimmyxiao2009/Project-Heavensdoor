using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Moments backed by RealServer's webhook-ingested 空间 feed (POST /webhook/space).
    /// Likes and comments are synchronized through RealServer's native QZone client.
    /// </summary>
    public sealed class RemoteMomentsService : IMomentsService
    {
        private readonly IGatewayService _remote;

        public RemoteMomentsService(IGatewayService remote)
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

        public async Task AddCommentAsync(Moment m, string text)
        {
            if (m == null || string.IsNullOrWhiteSpace(text)) return;
            var trimmed = text.Trim();
            if (!await _remote.SetSpaceCommentAsync(m.Id, trimmed))
                throw new InvalidOperationException("评论发送失败，请检查 QQ 空间登录状态后重试");
            m.Comments.Add(new MomentComment { Author = "我", Text = text.Trim() });
            m.RaiseCommentsChanged();
        }

        public Task<bool> GetEarlierFeedAsync()
            => _remote.GetEarlierSpaceFeedAsync();
    }
}
