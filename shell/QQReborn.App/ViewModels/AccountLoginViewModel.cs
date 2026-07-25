using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>Backs AccountLoginPage. NapCat local gateway: connect with host/port/password
    /// from Settings; no QR / Lagrange sign required.</summary>
    public class AccountLoginViewModel : ObservableObject
    {
        private readonly IProfileService _profile;

        public AccountLoginViewModel(IProfileService profile)
        {
            _profile = profile;
        }

        private string _statusText = "准备就绪";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private string _statusDetailText = "";
        public string StatusDetailText { get => _statusDetailText; set => Set(ref _statusDetailText, value); }

        private ImageSource _qrImage;
        public ImageSource QrImage { get => _qrImage; set => Set(ref _qrImage, value); }

        private bool _hasQrImage;
        public bool HasQrImage { get => _hasQrImage; set => Set(ref _hasQrImage, value); }

        private bool _showStartButton = true;
        public bool ShowStartButton { get => _showStartButton; set => Set(ref _showStartButton, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

        /// <summary>Unused on NapCat path (kept for wire compatibility).</summary>
        public string SignServerUrl { get; private set; } = "";
        public string SignToken { get; private set; } = "";
        public string SignUin { get; private set; } = "";

        public async Task LoadSettingsAsync()
        {
            var settings = await _profile.GetSettingsAsync();
            SignServerUrl = "";
            SignToken = "";
            // Optional: if user still has a QQ number in settings, send it for mismatch check.
            SignUin = settings.SignUin ?? "";

            StatusText = "准备连接网关";
            StatusDetailText = "将使用设置里的服务器地址、端口和访问密码。"
                + " 号在电脑 NapCat/NTQQ 中已登录即可，无需扫码。"
                + " 出门请把地址改成 OpenFrp/Frp 访问主机。";
            ShowStartButton = true;
        }
    }
}
