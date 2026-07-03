using System.Threading.Tasks;
using Windows.Storage;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// In-memory / LocalSettings-backed fake for the "我" area. Provides canned
    /// gamified extras and persists the handful of app toggles in ApplicationData
    /// so they survive a relaunch during dev.
    /// </summary>
    public class MockProfileService : IProfileService
    {
        private const string KeyNotifications = "qqr.settings.notifications";
        private const string KeyEnterToSend = "qqr.settings.enterToSend";
        private const string KeyFontSizeLevel = "qqr.settings.fontSizeLevel";
        private const string KeyServerHost = "qqr.settings.serverHost";

        private readonly ProfileExtras _extras = new ProfileExtras
        {
            Level = 12,
            IsVip = true,
            VipLabel = "超级会员",
            FavoriteCount = 86,
            AlbumCount = 14,
            FileCount = 23
        };

        public Task<ProfileExtras> GetExtrasAsync() => Task.FromResult(_extras);

        public Task<AppSettings> GetSettingsAsync()
        {
            var s = new AppSettings { DarkMode = true };
            var values = ApplicationData.Current.LocalSettings.Values;

            s.Notifications = values.TryGetValue(KeyNotifications, out var n) && n is bool nb ? nb : true;
            s.EnterToSend = values.TryGetValue(KeyEnterToSend, out var e) && e is bool eb ? eb : true;
            s.FontSizeLevel = values.TryGetValue(KeyFontSizeLevel, out var f) && f is int fi ? fi : 1;
            s.ServerHost = values.TryGetValue(KeyServerHost, out var h) && h is string hs && !string.IsNullOrWhiteSpace(hs) ? hs : "localhost";

            return Task.FromResult(s);
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            if (settings == null) return Task.CompletedTask;
            var values = ApplicationData.Current.LocalSettings.Values;
            values[KeyNotifications] = settings.Notifications;
            values[KeyEnterToSend] = settings.EnterToSend;
            values[KeyFontSizeLevel] = settings.FontSizeLevel;
            values[KeyServerHost] = string.IsNullOrWhiteSpace(settings.ServerHost) ? "localhost" : settings.ServerHost.Trim();
            return Task.CompletedTask;
        }

        public async Task<string> ClearCacheAsync()
        {
            // Pretend to scrub the cache for a beat, then report a plausible freed size.
            await Task.Delay(450);
            return "已清除 128.6 MB 缓存";
        }
    }
}
