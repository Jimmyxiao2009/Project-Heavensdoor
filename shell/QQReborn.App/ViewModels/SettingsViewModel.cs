using System;
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

        private bool _useSelfHostedSignServer;
        /// <summary>false = Lagrange's official community sign service (needs an API key from
        /// #signer registration), true = a self-hosted sign server the user points at directly.</summary>
        public bool UseSelfHostedSignServer
        {
            get => _useSelfHostedSignServer;
            set
            {
                if (Set(ref _useSelfHostedSignServer, value))
                {
                    RaisePropertyChanged(nameof(ShowOfficialSignFields));
                    Persist();
                }
            }
        }

        /// <summary>Inverse of <see cref="UseSelfHostedSignServer"/>, for showing the official-only fields.</summary>
        public bool ShowOfficialSignFields => !_useSelfHostedSignServer;

        private string _signServerUrl = "https://sign.lagrangecore.org";
        /// <summary>Only used/shown when UseSelfHostedSignServer is true.</summary>
        public string SignServerUrl
        {
            get => _signServerUrl;
            set { if (Set(ref _signServerUrl, value)) Persist(); }
        }

        private string _signToken = "";
        public string SignToken
        {
            get => _signToken;
            set { if (Set(ref _signToken, value)) Persist(); }
        }

        private string _signUin = "";
        public string SignUin
        {
            get => _signUin;
            set { if (Set(ref _signUin, value)) Persist(); }
        }

        private string _cacheStatus = "";
        public bool HasCacheStatus => !string.IsNullOrEmpty(_cacheStatus);
        public string CacheStatus
        {
            get => _cacheStatus;
            private set { if (Set(ref _cacheStatus, value)) RaisePropertyChanged(nameof(HasCacheStatus)); }
        }

        private string _downloadFolderPath = "";
        public string DownloadFolderPath
        {
            get => string.IsNullOrEmpty(_downloadFolderPath) ? "下载" : _downloadFolderPath;
            set => Set(ref _downloadFolderPath, value);
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
            _useSelfHostedSignServer = _settings.UseSelfHostedSignServer;
            _signServerUrl = string.IsNullOrWhiteSpace(_settings.SignServerUrl) ? "https://sign.lagrangecore.org" : _settings.SignServerUrl;
            _signToken = _settings.SignToken ?? "";
            _signUin = _settings.SignUin ?? "";
            RaisePropertyChanged(nameof(Notifications));
            RaisePropertyChanged(nameof(EnterToSend));
            RaisePropertyChanged(nameof(FontSizeLevel));
            RaisePropertyChanged(nameof(FontSizeText));
            RaisePropertyChanged(nameof(ServerHost));
            RaisePropertyChanged(nameof(UseSelfHostedSignServer));
            RaisePropertyChanged(nameof(ShowOfficialSignFields));
            RaisePropertyChanged(nameof(SignServerUrl));
            RaisePropertyChanged(nameof(SignToken));
            RaisePropertyChanged(nameof(SignUin));
        }

        public async Task ClearCacheAsync()
        {
            CacheStatus = "正在清除…";
            CacheStatus = await _profile.ClearCacheAsync();
        }

        /// <summary>ApplicationData LocalSettings caps a single composite value at roughly
        /// 8KB; stay well under that so a pasted wall of text into e.g. SignToken can't push
        /// the whole settings write over the limit.</summary>
        private const int MaxPersistedValueLength = 4096;

        private async void Persist()
        {
            if (!_isLoaded) return;
            try
            {
                _settings.Notifications = _notifications;
                _settings.EnterToSend = _enterToSend;
                _settings.FontSizeLevel = (int)System.Math.Round(_fontSizeLevel);
                _settings.ServerHost = Truncate(_serverHost);
                _settings.UseSelfHostedSignServer = _useSelfHostedSignServer;
                _settings.SignServerUrl = Truncate(_signServerUrl);
                _settings.SignToken = Truncate(_signToken);
                _settings.SignUin = Truncate(_signUin);
                await _profile.SaveSettingsAsync(_settings);
            }
            catch (Exception ex)
            {
                // A bad paste (huge string) or other storage failure shouldn't crash the app
                // via this async void -- there's no caller to catch it.
                System.Diagnostics.Debug.WriteLine("SettingsViewModel.Persist failed: " + ex);
            }
        }

        /// <summary>Values over <see cref="MaxPersistedValueLength"/> are dropped (not
        /// truncated-and-kept) rather than persisted half-cut, to avoid saving a mangled
        /// token/host that would silently break the setting it belongs to.</summary>
        private static string Truncate(string value)
        {
            if (value != null && value.Length > MaxPersistedValueLength) return string.Empty;
            return value;
        }
    }
}
