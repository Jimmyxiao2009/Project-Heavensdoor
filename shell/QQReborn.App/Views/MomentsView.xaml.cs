using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Popups;
using Windows.UI.Xaml.Shapes;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class MomentsView : UserControl
    {
        private readonly MomentsViewModel _vm;
        private readonly IGatewayService _remote;
        private readonly List<string> _pendingImages = new List<string>();
        private string _pendingVideo;

        public MomentsView()
        {
            InitializeComponent();
            _remote = AppServices.Gateway;
            _vm = new MomentsViewModel(AppServices.Moments);
            DataContext = _vm;
            Loaded += MomentsView_Loaded;
            Unloaded += MomentsView_Unloaded;
        }

        private int _spaceFeedEpoch;

        private async void MomentsView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_remote != null)
            {
                _remote.SpaceFeedUpdated -= Remote_SpaceFeedUpdated;
                _remote.SpaceFeedUpdated += Remote_SpaceFeedUpdated;
            }
            try
            {
                SyncText.Text = "同步中...";
                await _vm.LoadAsync();
                if (_remote != null) _vm.HasMore = _remote.SpaceFeedHasMore;
                SyncText.Text = "已同步";
            }
            catch
            {
                SyncText.Text = "同步失败";
            }
        }

        private void MomentsView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_remote != null) _remote.SpaceFeedUpdated -= Remote_SpaceFeedUpdated;
            // Invalidate any in-flight push refresh so it cannot touch unloaded UI.
            System.Threading.Interlocked.Increment(ref _spaceFeedEpoch);
        }

        private async void Remote_SpaceFeedUpdated(object sender, EventArgs e)
        {
            // Coalesce push storms (login + poll) so the feed is not rebuilt under the finger.
            var epoch = System.Threading.Interlocked.Increment(ref _spaceFeedEpoch);
            try
            {
                await Task.Delay(400);
                if (epoch != _spaceFeedEpoch) return;
                await _vm.RefreshAsync();
                if (epoch != _spaceFeedEpoch) return;
                if (_remote != null) _vm.HasMore = _remote.SpaceFeedHasMore;
                SyncText.Text = "已更新";
            }
            catch
            {
                if (epoch == _spaceFeedEpoch) SyncText.Text = "同步失败";
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshButton.IsEnabled = false;
            SyncText.Text = "同步中...";
            try
            {
                await _vm.RefreshAsync();
                if (_remote != null) _vm.HasMore = _remote.SpaceFeedHasMore;
                SyncText.Text = "已同步";
            }
            catch
            {
                SyncText.Text = "同步失败";
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
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
                try
                {
                    _pendingImages.Add(await CopyToLocalAsync(f));
                }
                catch (Exception)
                {
                    // Disk full / file in use / etc -- skip this one image rather than
                    // crashing the whole picker flow.
                }
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
            try
            {
                _pendingVideo = await CopyToLocalAsync(file);
            }
            catch (Exception)
            {
                // Disk full / file in use / etc -- leave _pendingVideo unset rather than crashing.
                return;
            }
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
                        Text = "",
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
            e.Handled = true;
            var path = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            UiScaleService.GetRootFrame()?.Navigate(typeof(VideoPlayerPage), path);
        }

        private void MomentImage_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            e.Handled = true;
            UiScaleService.GetRootFrame()?.Navigate(typeof(ImageViewerPage), path);
        }

        private void MomentCard_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            if (!((sender as FrameworkElement)?.DataContext is Moment moment)) return;
            e.Handled = true;
            UiScaleService.GetRootFrame()?.Navigate(typeof(MomentDetailPage), moment);
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
                if (string.IsNullOrWhiteSpace(t)) return;
                send.IsEnabled = false;
                try
                {
                    await _vm.AddCommentAsync(moment, t);
                    flyout.Hide();
                }
                catch (Exception ex)
                {
                    flyout.Hide();
                    var message = string.IsNullOrWhiteSpace(ex.Message)
                        ? "评论发送失败，请稍后重试"
                        : ex.Message;
                    await new MessageDialog(message, "动态评论").ShowAsync();
                }
                finally
                {
                    send.IsEnabled = true;
                }
            };

            flyout.ShowAt(btn);
        }

        /// <summary>Load older history pages of QQ 空间动态.</summary>
        private async void LoadMore_Click(object sender, RoutedEventArgs e)
        {
            LoadMoreButton.IsEnabled = false;
            LoadMoreButton.Content = "加载中...";
            try
            {
                var before = _vm.Feed.Count;
                var hasMore = await _vm.LoadMoreAsync();
                if (_remote != null) _vm.HasMore = hasMore && _remote.SpaceFeedHasMore;
                else _vm.HasMore = hasMore;

                if (!_vm.HasMore)
                {
                    LoadMoreButton.Content = before == _vm.Feed.Count && before > 0
                        ? "没有更多历史动态"
                        : "已加载全部动态";
                    LoadMoreButton.IsEnabled = false;
                }
                else
                {
                    LoadMoreButton.Content = "加载更多历史动态";
                    LoadMoreButton.IsEnabled = true;
                }
            }
            catch
            {
                LoadMoreButton.Content = "加载失败，点击重试";
                LoadMoreButton.IsEnabled = true;
            }
        }
    }
}
