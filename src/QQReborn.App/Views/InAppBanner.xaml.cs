using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace QQReborn.App.Views
{
    /// <summary>
    /// 应用内"新消息"横幅：顶部悬浮的圆角面板，含方形头像 + 标题 + 预览。
    /// 调用 Show(...) 显示，约 3.5 秒后自动隐藏；点击横幅触发回调。
    /// </summary>
    public sealed partial class InAppBanner : UserControl
    {
        private readonly DispatcherTimer _autoHide;
        private Action _onTap;

        public InAppBanner()
        {
            InitializeComponent();
            _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _autoHide.Tick += (s, e) => Hide();
        }

        /// <summary>填充内容并显示横幅；约 3.5 秒后自动隐藏；点击时调用 onTap。</summary>
        public void Show(string avatarPath, string title, string preview, Action onTap)
        {
            _onTap = onTap;

            try
            {
                AvatarBrush.ImageSource = string.IsNullOrEmpty(avatarPath)
                    ? null
                    : new BitmapImage(new Uri(avatarPath));
            }
            catch
            {
                AvatarBrush.ImageSource = null;
            }

            TitleText.Text = title ?? string.Empty;
            PreviewText.Text = preview ?? string.Empty;

            Visibility = Visibility.Visible;

            // 重新计时（若上一条尚未隐藏）。
            _autoHide.Stop();
            _autoHide.Start();
        }

        /// <summary>隐藏横幅并停止自动隐藏计时器。</summary>
        public void Hide()
        {
            _autoHide.Stop();
            Visibility = Visibility.Collapsed;
        }

        private void Bar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var tap = _onTap;
            Hide();
            tap?.Invoke();
        }
    }
}
