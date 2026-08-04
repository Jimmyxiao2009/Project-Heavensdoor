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

        public string VersionText => "QQ Reborn 0.2.0 · 为 Windows 10 Mobile 打造";

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

        // 0 = 小, 1 = 标准, 2 = 大 — bindable density tokens (never root ScaleTransform).
        private double _fontSizeLevel = 1;
        private int _uiScaleApplyEpoch;
        public double FontSizeLevel
        {
            get => _fontSizeLevel;
            set
            {
                if (Set(ref _fontSizeLevel, value))
                {
                    RaisePropertyChanged(nameof(FontSizeText));
                    Persist();
                    // Slider can fire multiple times while dragging; coalesce so we do not
                    // RaisePropertyChanged 20 size tokens on every tick.
                    var epoch = System.Threading.Interlocked.Increment(ref _uiScaleApplyEpoch);
                    var level = (int)System.Math.Round(value);
                    _ = ApplyUiScaleDebouncedAsync(epoch, level);
                }
            }
        }

        private async System.Threading.Tasks.Task ApplyUiScaleDebouncedAsync(int epoch, int level)
        {
            try { await System.Threading.Tasks.Task.Delay(120); }
            catch { return; }
            if (epoch != _uiScaleApplyEpoch) return;
            UiScaleService.ApplyLevel(level, persist: true);
        }

        public string FontSizeText
        {
            get
            {
                return UiScaleService.LabelForLevel((int)System.Math.Round(_fontSizeLevel));
            }
        }

        private string _serverHost = "localhost";
        /// <summary>Server host (no scheme). localhost / LAN IP / Frp host (OpenFrp/Sakura/etc.).</summary>
        public string ServerHost
        {
            get => _serverHost;
            set { if (Set(ref _serverHost, value)) Persist(); }
        }

        private string _serverPortText = "8765";
        /// <summary>Wire port as text for the TextBox. Default 8765; Frp remote port when outdoors.</summary>
        public string ServerPortText
        {
            get => _serverPortText;
            set { if (Set(ref _serverPortText, value)) Persist(); }
        }

        private string _accessPassword = "";
        public string AccessPassword
        {
            get => _accessPassword;
            set { if (Set(ref _accessPassword, value)) Persist(); }
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

        private bool _antiRecall = true;
        public bool AntiRecall
        {
            get => _antiRecall;
            set { if (Set(ref _antiRecall, value)) Persist(); }
        }

        private bool _showRevokeNotice = true;
        public bool ShowRevokeNotice
        {
            get => _showRevokeNotice;
            set { if (Set(ref _showRevokeNotice, value)) Persist(); }
        }

        private bool _doubleTapNudge = true;
        public bool DoubleTapNudge
        {
            get => _doubleTapNudge;
            set { if (Set(ref _doubleTapNudge, value)) Persist(); }
        }

        private bool _confirmBeforeSend;
        public bool ConfirmBeforeSend
        {
            get => _confirmBeforeSend;
            set { if (Set(ref _confirmBeforeSend, value)) Persist(); }
        }

        private bool _copyWithSender = true;
        public bool CopyWithSender
        {
            get => _copyWithSender;
            set { if (Set(ref _copyWithSender, value)) Persist(); }
        }

        private bool _vibrateOnMessage;
        public bool VibrateOnMessage
        {
            get => _vibrateOnMessage;
            set { if (Set(ref _vibrateOnMessage, value)) Persist(); }
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
            var port = _settings.ServerPort > 0 && _settings.ServerPort < 65536 ? _settings.ServerPort : 8765;
            _serverPortText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _accessPassword = _settings.AccessPassword ?? "";
            _antiRecall = _settings.AntiRecall;
            _showRevokeNotice = _settings.ShowRevokeNotice;
            _doubleTapNudge = _settings.DoubleTapNudge;
            _confirmBeforeSend = _settings.ConfirmBeforeSend;
            _copyWithSender = _settings.CopyWithSender;
            _vibrateOnMessage = _settings.VibrateOnMessage;
            RaisePropertyChanged(nameof(Notifications));
            RaisePropertyChanged(nameof(EnterToSend));
            RaisePropertyChanged(nameof(FontSizeLevel));
            RaisePropertyChanged(nameof(FontSizeText));
            // Ensure the saved density is active when opening Settings (also covers first run).
            UiScaleService.ApplyLevel((int)System.Math.Round(_fontSizeLevel), persist: false);
            RaisePropertyChanged(nameof(ServerHost));
            RaisePropertyChanged(nameof(ServerPortText));
            RaisePropertyChanged(nameof(AccessPassword));
            RaisePropertyChanged(nameof(AntiRecall));
            RaisePropertyChanged(nameof(ShowRevokeNotice));
            RaisePropertyChanged(nameof(DoubleTapNudge));
            RaisePropertyChanged(nameof(ConfirmBeforeSend));
            RaisePropertyChanged(nameof(CopyWithSender));
            RaisePropertyChanged(nameof(VibrateOnMessage));
        }

        public async Task ClearCacheAsync()
        {
            CacheStatus = "正在清除…";
            CacheStatus = await _profile.ClearCacheAsync();
        }

        /// <summary>ApplicationData LocalSettings caps a single composite value at roughly
        /// 8KB; stay well under that so a huge paste into host/password can't push
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
                int port;
                if (!int.TryParse((_serverPortText ?? "").Trim(), out port) || port <= 0 || port >= 65536)
                    port = 8765;
                // Same host sanitizer as the WebSocket client: accept pasted ws://host:port/ws.
                var host = Services.GatewayEndpoint.NormalizeServerHost(_serverHost ?? "", ref port);
                _settings.ServerHost = Truncate(host);
                _settings.ServerPort = port;
                // Keep the UI in sync when the user pasted a full URL into the host box.
                var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(_serverHost, host, System.StringComparison.Ordinal)
                    || !string.Equals(_serverPortText, portText, System.StringComparison.Ordinal))
                {
                    _serverHost = host;
                    _serverPortText = portText;
                    RaisePropertyChanged(nameof(ServerHost));
                    RaisePropertyChanged(nameof(ServerPortText));
                }
                _settings.AccessPassword = Truncate(_accessPassword);
                _settings.AntiRecall = _antiRecall;
                _settings.ShowRevokeNotice = _showRevokeNotice;
                _settings.DoubleTapNudge = _doubleTapNudge;
                _settings.ConfirmBeforeSend = _confirmBeforeSend;
                _settings.CopyWithSender = _copyWithSender;
                _settings.VibrateOnMessage = _vibrateOnMessage;
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
            if (value == null) return null;
            value = value.Trim();
            if (value.Length > MaxPersistedValueLength) return string.Empty;
            return value;
        }
    }
}
