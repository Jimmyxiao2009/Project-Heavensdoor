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
    /// App.ChatService is a IGatewayService pointed at QQReborn.RealServer -- against the
    /// mock backend (or the fake demo server) this just reports that remote isn't enabled.
    /// </summary>
    public sealed partial class AccountLoginPage : Page
    {
        private readonly AccountLoginViewModel _vm;
        private readonly IGatewayService _remote;

        public AccountLoginPage()
        {
            InitializeComponent();
            _vm = new AccountLoginViewModel(new MockProfileService());
            _remote = AppServices.Gateway;
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
            // IGatewayService is an app-wide singleton (App.ChatService) but this page is
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
            _vm.StatusDetailText = "正在连接电脑网关并绑定 NapCat，请稍候…";
            try
            {
                // Bind whatever NapCat is currently logged in as.
                var accepted = await _remote.ConfigureAccountAsync();
                if (!accepted)
                {
                    _vm.StatusText = "连接网关失败";
                    _vm.StatusDetailText = "请确认电脑管家已启动、访问密码正确，且 NapCat/NTQQ 已登录。";
                }
                else
                {
                    _vm.StatusText = "已连接";
                    _vm.StatusDetailText = "本机网关已绑定 NapCat 当前登录号，可返回会话列表收发消息。";
                    _vm.ShowStartButton = false;
                }
            }
            catch (Exception ex)
            {
                var msg = RemoteChatService.FormatSocketError("连接失败", ex);
                var detail = (ex.Message ?? "") + "\n" + msg;
                // Only the server explicit rejection is a wrong-password failure.
                // Auth-timeout messages also mention access password and must not show as wrong password.
                if (detail.IndexOf("访问密码错误", StringComparison.Ordinal) >= 0
                    || detail.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _vm.StatusText = "访问密码错误";
                    _vm.StatusDetailText = "请到设置中填写与电脑管家一致的访问密码后重试。当前管家密码在管家里可复制。";
                }
                else if (detail.IndexOf("鉴权超时", StringComparison.Ordinal) >= 0)
                {
                    _vm.StatusText = "网关鉴权超时";
                    _vm.StatusDetailText = "已连上地址，但鉴权无响应。请确认管家网关在线，且设置里的访问密码与管家完全一致后重试。";
                }
                else if (msg.IndexOf("0x8000000E", StringComparison.OrdinalIgnoreCase) >= 0
                         || ex.HResult == unchecked((int)0x8000000E))
                {
                    _vm.StatusText = "连接状态异常";
                    _vm.StatusDetailText = "0x8000000E：WebSocket 连接被复用/半开。请完全退出 App 后重开，确认管家网关在线与访问密码一致后再点「开始连接」。";
                }
                else
                {
                    _vm.StatusText = "连接失败";
                    _vm.StatusDetailText = msg;
                }
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
