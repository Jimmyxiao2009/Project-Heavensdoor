using System;
using System.Collections.Generic;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class MomentsView : UserControl
    {
        private readonly MomentsViewModel _vm;
        private readonly List<string> _pendingImages = new List<string>();
        private string _pendingVideo;

        public MomentsView()
        {
            InitializeComponent();
            _vm = new MomentsViewModel(new MockMomentsService());
            DataContext = _vm;
            Loaded += MomentsView_Loaded;
        }

        private async void MomentsView_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.LoadAsync();
        }

        private void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            var text = PublishBox.Text;
            _vm.PublishLocal(text, _pendingImages, _pendingVideo);
            PublishBox.Text = string.Empty;
            _pendingImages.Clear();
            _pendingVideo = null;
            RefreshAttachPreview();
        }

        private async void PickImages_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            foreach (var f in files)
            {
                if (_pendingImages.Count >= 9) break;
                _pendingImages.Add(await CopyToLocalAsync(f));
            }
            RefreshAttachPreview();
        }

        private async void PickVideo_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.VideosLibrary };
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".wmv");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mkv");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            _pendingVideo = await CopyToLocalAsync(file);
            RefreshAttachPreview();
        }

        private static async System.Threading.Tasks.Task<string> CopyToLocalAsync(StorageFile file)
        {
            var copy = await file.CopyAsync(ApplicationData.Current.LocalFolder,
                "moment_" + Guid.NewGuid().ToString("N") + file.FileType, NameCollisionOption.GenerateUniqueName);
            return "ms-appdata:///local/" + copy.Name;
        }

        private void RefreshAttachPreview()
        {
            AttachPreview.Children.Clear();
            foreach (var path in _pendingImages)
            {
                AttachPreview.Children.Add(new Rectangle
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 6, 0),
                    Fill = new ImageBrush { ImageSource = new BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill }
                });
            }
            if (!string.IsNullOrEmpty(_pendingVideo))
            {
                AttachPreview.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    Background = (Brush)Application.Current.Resources["MetroSubtleBrush"],
                    Child = new TextBlock
                    {
                        Text = "🎬",
                        FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            AttachPreview.Visibility = (AttachPreview.Children.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            var moment = (sender as Button)?.Tag as Moment;
            if (moment != null) await _vm.ToggleLikeAsync(moment);
        }

        private void VideoPoster_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            (Window.Current.Content as Frame)?.Navigate(typeof(VideoPlayerPage), path);
        }

        // Tapping 💬 opens a tiny inline composer flyout anchored to the button.
        private void CommentButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var moment = btn != null ? btn.Tag as Moment : null;
            if (moment == null) return;

            var input = new TextBox
            {
                PlaceholderText = "写评论…",
                Width = 220,
                AcceptsReturn = false
            };
            var send = new Button
            {
                Content = "发送",
                Margin = new Thickness(8, 0, 0, 0)
            };

            var flyout = new Flyout();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(input);
            panel.Children.Add(send);
            flyout.Content = panel;

            send.Click += async (s, args) =>
            {
                var t = input.Text;
                if (!string.IsNullOrWhiteSpace(t))
                {
                    await _vm.AddCommentAsync(moment, t);
                }
                flyout.Hide();
            };

            flyout.ShowAt(btn);
        }
    }
}
