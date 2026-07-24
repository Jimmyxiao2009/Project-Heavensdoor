using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>Backs AccountLoginPage. Reads sign-server config from Settings; the actual
    /// QR/status updates are pushed into this VM by the page's code-behind (which owns the
    /// RemoteChatService subscription -- see AccountLoginPage.xaml.cs).</summary>
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

        /// <summary>Sign server URL to use -- the official Lagrange URL unless the user
        /// opted into a self-hosted one in Settings.</summary>
        public string SignServerUrl { get; private set; } = "https://sign.lagrangecore.org";
        public string SignToken { get; private set; } = "";
        public string SignUin { get; private set; } = "";

        public async Task LoadSettingsAsync()
        {
            var settings = await _profile.GetSettingsAsync();
            SignServerUrl = settings.UseSelfHostedSignServer
                ? settings.SignServerUrl
                : "https://sign.lagrangecore.org";
            SignToken = settings.SignToken ?? "";
            SignUin = settings.SignUin ?? "";

            if (string.IsNullOrWhiteSpace(SignUin))
            {
                StatusText = "还没填QQ号";
                StatusDetailText = "先去设置页的\"账号\"栏填 API Key 和 QQ 号，再回来点开始登录";
                ShowStartButton = false;
            }
            else
            {
                StatusText = "准备就绪";
                StatusDetailText = "点击开始登录，扫码用的二维码会出现在这里";
            }
        }
    }
}
