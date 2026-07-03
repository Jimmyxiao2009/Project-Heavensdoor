using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>Backs the Settings page. Loads + persists the handful of app toggles.</summary>
    public class SettingsViewModel : ObservableObject
    {
        private readonly IProfileService _profile;
        private AppSettings _settings = new AppSettings();
        private bool _isLoaded;

        public SettingsViewModel(IProfileService profile)
        {
            _profile = profile;
        }

        public string VersionText => "QQ Reborn 0.1 · 为 Windows 10 Mobile 打造";

        private bool _darkMode = true;
        public bool DarkMode { get => _darkMode; private set => Set(ref _darkMode, value); }

        private bool _notifications = true;
        public bool Notifications
        {
            get => _notifications;
            set { if (Set(ref _notifications, value)) Persist(); }
        }

        private bool _enterToSend = true;
        public bool EnterToSend
        {
            get => _enterToSend;
            set { if (Set(ref _enterToSend, value)) Persist(); }
        }

        // 0 = 小, 1 = 标准, 2 = 大
        private double _fontSizeLevel = 1;
        public double FontSizeLevel
        {
            get => _fontSizeLevel;
            set
            {
                if (Set(ref _fontSizeLevel, value))
                {
                    RaisePropertyChanged(nameof(FontSizeText));
                    Persist();
                }
            }
        }

        public string FontSizeText
        {
            get
            {
                int level = (int)System.Math.Round(_fontSizeLevel);
                if (level <= 0) return "小";
                if (level >= 2) return "大";
                return "标准";
            }
        }

        private string _serverHost = "localhost";
        /// <summary>Server host (no scheme/port) used by the remote backend. On a phone set the
        /// PC's LAN IP so the device can reach the server; "localhost" is the desktop default.</summary>
        public string ServerHost
        {
            get => _serverHost;
            set { if (Set(ref _serverHost, value)) Persist(); }
        }

        private string _cacheStatus = "";
        public bool HasCacheStatus => !string.IsNullOrEmpty(_cacheStatus);
        public string CacheStatus
        {
            get => _cacheStatus;
            private set { if (Set(ref _cacheStatus, value)) RaisePropertyChanged(nameof(HasCacheStatus)); }
        }

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            _settings = await _profile.GetSettingsAsync() ?? new AppSettings();
            DarkMode = _settings.DarkMode;
            // Use backing fields-via-setters but suppress the persist storm during load.
            _notifications = _settings.Notifications;
            _enterToSend = _settings.EnterToSend;
            _fontSizeLevel = _settings.FontSizeLevel;
            _serverHost = string.IsNullOrWhiteSpace(_settings.ServerHost) ? "localhost" : _settings.ServerHost;
            RaisePropertyChanged(nameof(Notifications));
            RaisePropertyChanged(nameof(EnterToSend));
            RaisePropertyChanged(nameof(FontSizeLevel));
            RaisePropertyChanged(nameof(FontSizeText));
            RaisePropertyChanged(nameof(ServerHost));
        }

        public async Task ClearCacheAsync()
        {
            CacheStatus = "正在清除…";
            CacheStatus = await _profile.ClearCacheAsync();
        }

        private async void Persist()
        {
            if (!_isLoaded) return;
            _settings.Notifications = _notifications;
            _settings.EnterToSend = _enterToSend;
            _settings.FontSizeLevel = (int)System.Math.Round(_fontSizeLevel);
            _settings.ServerHost = _serverHost;
            await _profile.SaveSettingsAsync(_settings);
        }
    }
}
