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
        private const string KeyServerPort = "qqr.settings.serverPort";
        private const string KeyAccessPassword = "qqr.settings.accessPassword";
        private const string KeyUseSelfHostedSignServer = "qqr.settings.useSelfHostedSignServer";
        private const string KeySignServerUrl = "qqr.settings.signServerUrl";
        private const string KeySignToken = "qqr.settings.signToken";
        private const string KeySignUin = "qqr.settings.signUin";
        private const string KeyAntiRecall = UtilitySettings.KeyAntiRecall;
        private const string KeyShowRevokeNotice = UtilitySettings.KeyShowRevokeNotice;
        private const string KeyDoubleTapNudge = UtilitySettings.KeyDoubleTapNudge;
        private const string KeyConfirmBeforeSend = UtilitySettings.KeyConfirmBeforeSend;
        private const string KeyCopyWithSender = UtilitySettings.KeyCopyWithSender;
        private const string KeyVibrateOnMessage = UtilitySettings.KeyVibrateOnMessage;

        private readonly ProfileExtras _extras = new ProfileExtras
        {
            Level = 12,
            IsVip = true,
            VipLabel = "超级会员"
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
            if (values.TryGetValue(KeyServerPort, out var sp) && sp is int spi && spi > 0 && spi < 65536)
                s.ServerPort = spi;
            else if (values.TryGetValue(KeyServerPort, out var sps) && sps is string spsStr && int.TryParse(spsStr, out var spp) && spp > 0 && spp < 65536)
                s.ServerPort = spp;
            else
                s.ServerPort = 8765;
            s.AccessPassword = values.TryGetValue(KeyAccessPassword, out var ap) && ap is string aps ? aps : "";
            s.UseSelfHostedSignServer = values.TryGetValue(KeyUseSelfHostedSignServer, out var sh) && sh is bool shb && shb;
            s.SignServerUrl = values.TryGetValue(KeySignServerUrl, out var su) && su is string sus && !string.IsNullOrWhiteSpace(sus) ? sus : "https://sign.lagrangecore.org";
            s.SignToken = values.TryGetValue(KeySignToken, out var st) && st is string sts ? sts : "";
            s.SignUin = values.TryGetValue(KeySignUin, out var sui) && sui is string suis ? suis : "";
            s.AntiRecall = !(values.TryGetValue(KeyAntiRecall, out var ar) && ar is bool arb) || arb;
            s.ShowRevokeNotice = !(values.TryGetValue(KeyShowRevokeNotice, out var sr) && sr is bool srb) || srb;
            s.DoubleTapNudge = !(values.TryGetValue(KeyDoubleTapNudge, out var dn) && dn is bool dnb) || dnb;
            s.ConfirmBeforeSend = values.TryGetValue(KeyConfirmBeforeSend, out var cb) && cb is bool cbb && cbb;
            s.CopyWithSender = !(values.TryGetValue(KeyCopyWithSender, out var cs) && cs is bool csb) || csb;
            s.VibrateOnMessage = values.TryGetValue(KeyVibrateOnMessage, out var vm) && vm is bool vmb && vmb;

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
            var port = settings.ServerPort > 0 && settings.ServerPort < 65536 ? settings.ServerPort : 8765;
            values[KeyServerPort] = port;
            values[KeyAccessPassword] = settings.AccessPassword ?? "";
            values[KeyUseSelfHostedSignServer] = settings.UseSelfHostedSignServer;
            values[KeySignServerUrl] = string.IsNullOrWhiteSpace(settings.SignServerUrl) ? "https://sign.lagrangecore.org" : settings.SignServerUrl.Trim();
            values[KeySignToken] = settings.SignToken ?? "";
            values[KeySignUin] = settings.SignUin ?? "";
            values[KeyAntiRecall] = settings.AntiRecall;
            values[KeyShowRevokeNotice] = settings.ShowRevokeNotice;
            values[KeyDoubleTapNudge] = settings.DoubleTapNudge;
            values[KeyConfirmBeforeSend] = settings.ConfirmBeforeSend;
            values[KeyCopyWithSender] = settings.CopyWithSender;
            values[KeyVibrateOnMessage] = settings.VibrateOnMessage;
            return Task.CompletedTask;
        }

        public async Task<string> ClearCacheAsync()
        {
            // Pretend to scrub the cache for a beat. No real size is tracked, so report
            // an honest generic confirmation rather than a made-up freed-size number.
            await Task.Delay(450);
            return "缓存已清除";
        }
    }
}
