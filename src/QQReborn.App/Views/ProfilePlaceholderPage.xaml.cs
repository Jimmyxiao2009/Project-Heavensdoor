using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Lightweight stand-in for the not-yet-built 我 sub-pages (收藏 / 相册 / 文件 / 装扮).
    /// Picks its title + glyph from the string parameter passed by ProfileView.
    /// </summary>
    public sealed partial class ProfilePlaceholderPage : Page
    {
        public ProfilePlaceholderPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var key = e.Parameter as string;
            switch (key)
            {
                case "album":
                    TitleText.Text = "我的相册";
                    GlyphText.Text = "📷";
                    HintText.Text = "登录后同步你的 QQ 空间相册";
                    break;
                case "files":
                    TitleText.Text = "我的文件";
                    GlyphText.Text = "📁";
                    HintText.Text = "登录后查看微云 / 群文件";
                    break;
                case "dressup":
                    TitleText.Text = "个性装扮";
                    GlyphText.Text = "🎨";
                    HintText.Text = "气泡、主题、挂件稍后接入";
                    break;
                default:
                    TitleText.Text = "我的收藏";
                    GlyphText.Text = "⭐";
                    HintText.Text = "登录后同步你 QQ 里收藏的内容";
                    break;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }
    }
}
