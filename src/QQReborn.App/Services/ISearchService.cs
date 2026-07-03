using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Global search over contacts, group conversations and chat-message text.
    /// The mock implementation reads the live <see cref="IChatService"/> singleton
    /// (read-only) plus its own extra fake directory so results feel rich.
    /// </summary>
    public interface ISearchService
    {
        /// <summary>
        /// Returns grouped results (联系人 / 群聊 / 聊天记录) for <paramref name="query"/>.
        /// Empty/whitespace query yields an empty list.
        /// </summary>
        Task<IReadOnlyList<SearchSection>> SearchAsync(string query);
    }
}
