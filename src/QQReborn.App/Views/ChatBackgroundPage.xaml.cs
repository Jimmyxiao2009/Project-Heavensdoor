using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace QQReborn.App.Views
{
    /// <summary>
    /// 聊天背景选择页。点击色块/图片即写入 LocalSettings 的 "ChatBackground" 键。
    /// 取值约定（与会话页读取端共享）：
    ///   缺省或 "" -> 默认黑底
    ///   "#AARRGGBB" -> 纯色
    ///   "ms-appx..." -> 图片
    /// 完全自包含，仅依赖 LocalSettings；无导航参数。
    /// </summary>
    public sealed partial class ChatBackgroundPage : Page
    {
        // 与会话背景读取端共享的设置键名。
        private const string SettingsKey = "ChatBackground";

        private readonly List<BackgroundSwatch> _solids = new List<BackgroundSwatch>();
        private readonly List<BackgroundSwatch> _images = new List<BackgroundSwatch>();

        public ChatBackgroundPage()
        {
            InitializeComponent();
            BuildSwatches();
            SolidGrid.ItemsSource = _solids;
            ImageGrid.ItemsSource = _images;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 进入时按当前已保存值预选。
            ApplySelection(ReadCurrent());
        }

        private void BuildSwatches()
        {
            // 默认黑 + 纯色：Value 为写入设置的字符串（""=默认）。
            _solids.Add(new BackgroundSwatch("默认(黑)", string.Empty, new SolidColorBrush(Colors.Black)));
            _solids.Add(new BackgroundSwatch("深蓝", "#FF0E1A2B", SolidFromHex("#FF0E1A2B")));
            _solids.Add(new BackgroundSwatch("墨绿", "#FF0E1F18", SolidFromHex("#FF0E1F18")));
            _solids.Add(new BackgroundSwatch("深紫", "#FF1A0E2B", SolidFromHex("#FF1A0E2B")));
            _solids.Add(new BackgroundSwatch("暗红", "#FF2B0E12", SolidFromHex("#FF2B0E12")));

            // 图片：Value 为 ms-appx 路径，预览使用同路径的 BitmapImage。
            AddImageSwatch("方块", "ms-appx:///Assets/Square310x310Logo.scale-200.png");
            AddImageSwatch("宽幅", "ms-appx:///Assets/Wide310x150Logo.scale-200.png");
            AddImageSwatch("启动图", "ms-appx:///Assets/SplashScreen.scale-200.png");
        }

        private void AddImageSwatch(string label, string path)
        {
            ImageSource src = null;
            try { src = new BitmapImage(new Uri(path)); }
            catch (Exception) { }
            _images.Add(new BackgroundSwatch(label, path, null) { ImageSource = src });
        }

        private void Swatch_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is BackgroundSwatch swatch)) return;

            // 写入设置（约定值）。
            ApplicationData.Current.LocalSettings.Values[SettingsKey] = swatch.Value;

            // 同步选中标记。
            ApplySelection(swatch.Value);

            // 选完返回，会话页在 OnNavigatedTo 重新读取并应用。
            if (this.Frame != null && this.Frame.CanGoBack) this.Frame.GoBack();
        }

        private void ApplySelection(string current)
        {
            foreach (var s in _solids) s.IsSelected = string.Equals(s.Value, current, StringComparison.Ordinal);
            foreach (var s in _images) s.IsSelected = string.Equals(s.Value, current, StringComparison.Ordinal);
        }

        /// <summary>读取当前已保存值，归一化为约定字符串（缺省 -> "")。</summary>
        private static string ReadCurrent()
        {
            var raw = ApplicationData.Current.LocalSettings.Values[SettingsKey] as string;
            return raw ?? string.Empty;
        }

        private static SolidColorBrush SolidFromHex(string hex)
        {
            return new SolidColorBrush(ColorFromHex(hex));
        }

        /// <summary>解析 "#AARRGGBB"（或 "#RRGGBB"）为 Color，失败回退黑色。</summary>
        private static Color ColorFromHex(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return Colors.Black;
                var s = hex.TrimStart('#');
                byte a = 0xFF, r, g, b;
                if (s.Length == 8)
                {
                    a = Convert.ToByte(s.Substring(0, 2), 16);
                    r = Convert.ToByte(s.Substring(2, 2), 16);
                    g = Convert.ToByte(s.Substring(4, 2), 16);
                    b = Convert.ToByte(s.Substring(6, 2), 16);
                }
                else if (s.Length == 6)
                {
                    r = Convert.ToByte(s.Substring(0, 2), 16);
                    g = Convert.ToByte(s.Substring(2, 2), 16);
                    b = Convert.ToByte(s.Substring(4, 2), 16);
                }
                else
                {
                    return Colors.Black;
                }
                return Color.FromArgb(a, r, g, b);
            }
            catch (Exception)
            {
                return Colors.Black;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame != null && this.Frame.CanGoBack) this.Frame.GoBack();
        }

        /// <summary>单个背景选项（自包含视图模型，仅本页使用）。</summary>
        private sealed class BackgroundSwatch : INotifyPropertyChanged
        {
            private bool _isSelected;

            public BackgroundSwatch(string label, string value, Brush preview)
            {
                Label = label;
                Value = value;
                Preview = preview;
            }

            /// <summary>显示名称。</summary>
            public string Label { get; }

            /// <summary>写入 LocalSettings 的约定值（""/#hex/ms-appx）。</summary>
            public string Value { get; }

            /// <summary>纯色预览画刷（图片项为 null）。</summary>
            public Brush Preview { get; }

            /// <summary>图片预览源（纯色项为 null）。</summary>
            public ImageSource ImageSource { get; set; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string name = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
