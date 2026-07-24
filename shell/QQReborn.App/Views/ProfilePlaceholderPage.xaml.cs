using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace QQReborn.App.Views
{
    public sealed partial class ProfilePlaceholderPage : Page
    {
        private readonly ObservableCollection<ResourceItem> _items = new ObservableCollection<ResourceItem>();
        private string _key;

        public ProfilePlaceholderPage()
        {
            InitializeComponent();
            ResourceList.ItemsSource = _items;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _key = e.Parameter as string ?? "favorites";
            _items.Clear();
            ResourceList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;

            switch (_key)
            {
                case "files":
                    TitleText.Text = "我的文件";
                    GlyphText.Glyph = "\uE8A5";
                    HintText.Text = "显示本机已发送或缓存的文件";
                    await LoadLocalFilesAsync();
                    break;
                case "favorites":
                default:
                    TitleText.Text = "我的收藏";
                    GlyphText.Glyph = "\uE734";
                    HintText.Text = "本地收藏功能已准备，聊天内可继续接入收藏动作";
                    EmptyTitle.Text = "暂无本地收藏";
                    break;
            }
        }

        private async System.Threading.Tasks.Task LoadLocalFilesAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync("OutgoingFiles");
                foreach (var file in await folder.GetFilesAsync())
                {
                    _items.Add(new ResourceItem
                    {
                        Name = file.Name,
                        Detail = FormatSize((await file.GetBasicPropertiesAsync()).Size) + " · 本地缓存",
                        Glyph = "\uE8A5",
                        Path = file.Path
                    });
                }
                if (_items.Count > 0)
                {
                    EmptyState.Visibility = Visibility.Collapsed;
                    ResourceList.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private async void ResourceList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ResourceItem;
            if (item == null || string.IsNullOrEmpty(item.Path)) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                HintText.Text = "打开文件失败：" + ex.Message;
            }
        }

        private static string FormatSize(ulong bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.#") + " KB";
            return (bytes / (1024d * 1024d)).ToString("0.##") + " MB";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        private sealed class ResourceItem
        {
            public string Name { get; set; }
            public string Detail { get; set; }
            public string Glyph { get; set; }
            public string Path { get; set; }
        }
    }
}
