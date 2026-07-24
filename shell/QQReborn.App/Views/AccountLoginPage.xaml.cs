using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Real-account QR login, reached from Settings' "登录 QQ" row. Only functional when
    /// App.ChatService is a RemoteChatService pointed at QQReborn.RealServer -- against the
    /// mock backend (or the fake demo server) this just reports that remote isn't enabled.
    /// </summary>
    public sealed partial class AccountLoginPage : Page
    {
        private readonly AccountLoginViewModel _vm;
        private readonly RemoteChatService _remote;

        public AccountLoginPage()
        {
            InitializeComponent();
            _vm = new AccountLoginViewModel(new MockProfileService());
            _remote = App.ChatService as RemoteChatService;
            DataContext = _vm;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (_remote == null)
            {
                _vm.StatusText = "远程后端未启用";
                _vm.StatusDetailText = "把 App.xaml.cs 里的 UseRemoteBackend 改成 true、重新编译并连上 QQReborn.RealServer 后再来";
                _vm.ShowStartButton = false;
                return;
            }

            _remote.QrCodeReceived += OnQrCodeReceived;
            _remote.LoginStatusChanged += OnLoginStatusChanged;
            await _vm.LoadSettingsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // RemoteChatService is an app-wide singleton (App.ChatService) but this page is
            // transient -- without unsubscribing, repeat visits would stack duplicate handlers.
            if (_remote != null)
            {
                _remote.QrCodeReceived -= OnQrCodeReceived;
                _remote.LoginStatusChanged -= OnLoginStatusChanged;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        private async void StartLoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (_remote == null || _vm.IsBusy) return;
            _vm.IsBusy = true;
            _vm.StatusText = "连接中…";
            _vm.StatusDetailText = "";
            try
            {
                var accepted = await _remote.ConfigureAccountAsync(_vm.SignServerUrl, _vm.SignToken, _vm.SignUin);
                if (!accepted)
                {
                    _vm.StatusText = "启动登录失败";
                    _vm.StatusDetailText = "服务器拒绝了请求（可能已经在线，或者已有一次登录正在进行）";
                }
            }
            catch (Exception ex)
            {
                _vm.StatusText = "连接失败";
                _vm.StatusDetailText = ex.Message;
            }
            finally
            {
                _vm.IsBusy = false;
            }
        }

        private async void OnQrCodeReceived(object sender, QrCodeInfo info)
        {
            try
            {
                var bytes = Convert.FromBase64String(info.ImageBase64);
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(stream);
                    _vm.QrImage = bmp;
                }
                _vm.HasQrImage = true;
                _vm.ShowStartButton = false;
            }
            catch (Exception ex)
            {
                _vm.StatusDetailText = "二维码解析失败: " + ex.Message;
            }
        }

        private void OnLoginStatusChanged(object sender, LoginStatusInfo info)
        {
            switch (info.State)
            {
                case "waitingForScan":
                    _vm.StatusText = "请扫码";
                    _vm.StatusDetailText = "用另一台已登录 QQ 的设备扫描上面的二维码";
                    break;
                case "waitingForConfirm":
                    _vm.StatusText = "已扫描，等待确认";
                    _vm.StatusDetailText = "在扫码的设备上确认登录";
                    break;
                case "online":
                    _vm.StatusText = "登录成功";
                    _vm.StatusDetailText = $"已上线（QQ {info.Uin}）";
                    _vm.HasQrImage = false;
                    _vm.ShowStartButton = false;
                    break;
                case "offline":
                    _vm.StatusText = "已下线";
                    _vm.StatusDetailText = info.Message ?? "";
                    _vm.ShowStartButton = true;
                    break;
                case "expired":
                    _vm.StatusText = "二维码已过期";
                    _vm.StatusDetailText = "点击重新开始登录";
                    _vm.HasQrImage = false;
                    _vm.ShowStartButton = true;
                    break;
                case "canceled":
                    _vm.StatusText = "已取消";
                    _vm.StatusDetailText = "";
                    _vm.HasQrImage = false;
                    _vm.ShowStartButton = true;
                    break;
                case "failed":
                    _vm.StatusText = "登录失败";
                    _vm.StatusDetailText = info.Message ?? "";
                    _vm.HasQrImage = false;
                    _vm.ShowStartButton = true;
                    break;
            }
        }
    }
}
