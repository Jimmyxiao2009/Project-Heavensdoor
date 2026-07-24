using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Abstraction over the friend-feed (好友动态 / QQ空间) backend. The mock
    /// implementation drives the UI today; a real implementation can replace it later.
    /// </summary>
    public interface IMomentsService
    {
        Task<IReadOnlyList<Moment>> GetFeedAsync();

        /// <summary>Toggle the like state of a moment and adjust its count.</summary>
        Task ToggleLikeAsync(Moment m);

        /// <summary>Append a comment authored by the logged-in user.</summary>
        Task AddCommentAsync(Moment m, string text);

        /// <summary>Load older history pages. Returns true if more pages exist.</summary>
        Task<bool> GetEarlierFeedAsync();
    }
}
