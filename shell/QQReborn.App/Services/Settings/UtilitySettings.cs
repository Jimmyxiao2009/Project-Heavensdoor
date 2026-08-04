using Windows.Storage;

namespace QQReborn.App.Services
{
    /// <summary>
    /// LocalSettings-backed practical toggles (防撤回 / 戳一戳 / etc).
    /// Shared keys with <see cref="MockProfileService"/> / SettingsViewModel.
    /// </summary>
    public static class UtilitySettings
    {
        public const string KeyAntiRecall = "qqr.settings.antiRecall";
        public const string KeyShowRevokeNotice = "qqr.settings.showRevokeNotice";
        public const string KeyDoubleTapNudge = "qqr.settings.doubleTapNudge";
        public const string KeyConfirmBeforeSend = "qqr.settings.confirmBeforeSend";
        public const string KeyCopyWithSender = "qqr.settings.copyWithSender";
        public const string KeyVibrateOnMessage = "qqr.settings.vibrateOnMessage";

        public static bool AntiRecall => GetBool(KeyAntiRecall, true);
        public static bool ShowRevokeNotice => GetBool(KeyShowRevokeNotice, true);
        public static bool DoubleTapNudge => GetBool(KeyDoubleTapNudge, true);
        public static bool ConfirmBeforeSend => GetBool(KeyConfirmBeforeSend, false);
        public static bool CopyWithSender => GetBool(KeyCopyWithSender, true);
        public static bool VibrateOnMessage => GetBool(KeyVibrateOnMessage, false);

        public static bool GetBool(string key, bool defaultValue)
        {
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[key];
                if (raw is bool b) return b;
            }
            catch { }
            return defaultValue;
        }

        public static void SetBool(string key, bool value)
        {
            try { ApplicationData.Current.LocalSettings.Values[key] = value; }
            catch { }
        }
    }
}
