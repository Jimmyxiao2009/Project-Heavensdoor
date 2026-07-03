using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Cosmetic profile extras for the "我" area (level, VIP, counts, app settings).
    /// Self identity (nickname/avatar/signature/uin) still comes from IChatService;
    /// this service only supplies the decorative bits the chat backend does not model.
    /// </summary>
    public interface IProfileService
    {
        Task<ProfileExtras> GetExtrasAsync();

        Task<AppSettings> GetSettingsAsync();

        Task SaveSettingsAsync(AppSettings settings);

        /// <summary>Pretend to clear the local cache; returns a human readable freed-size string.</summary>
        Task<string> ClearCacheAsync();
    }
}
