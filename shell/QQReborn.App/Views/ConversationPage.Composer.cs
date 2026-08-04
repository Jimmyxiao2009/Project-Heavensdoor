using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
// ImageGalleryArgs / ImageGalleryItem live in Models
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Storage.AccessCache;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ConversationPage
    {
        // ---- emoji / sticker panel ----

        private async void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            // Recording writes RecordingHint.Text on a 1s DispatcherTimer tick. Just hiding
            // RecordingBar (the old behavior) left that timer running against a now-invisible
            // control and never sent the in-progress clip. Stop/send it the same way the mic
            // button's second tap does, so the button and the panel agree on recording state.
            if (_isRecording) await StopRecordingAsync(send: true);

            RecordingBar.Visibility = Visibility.Collapsed;
            EmojiPanel.Visibility = EmojiPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Tab_Emoji(object sender, TappedRoutedEventArgs e) => SelectTab(0);
        private void Tab_Classic(object sender, TappedRoutedEventArgs e) => SelectTab(1);
        private void Tab_Fav(object sender, TappedRoutedEventArgs e) => SelectTab(2);

        private void SelectTab(int index)
        {
            EmojiGrid.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            StickerGrid.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            FavPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            var on = (Brush)Application.Current.Resources["MetroPrimaryTextBrush"];
            var off = (Brush)Application.Current.Resources["MetroSecondaryTextBrush"];
            TabEmoji.Foreground = index == 0 ? on : off;
            TabClassic.Foreground = index == 1 ? on : off;
            TabFav.Foreground = index == 2 ? on : off;
        }

        private void Emoji_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string emoji) _vm.InsertEmoji(emoji);
        }

        private async void Sticker_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string sticker)
            {
                await _vm.SendStickerAsync(sticker);
                ScrollToBottom();
            }
        }

        // ---- image ----

        private async void ImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            IReadOnlyList<StorageFile> files;
            try
            {
                files = await picker.PickMultipleFilesAsync();
            }
            catch (Exception ex)
            {
                _vm.AppendSystem("选择图片失败：" + ex.Message);
                ScrollToBottom();
                return;
            }
            if (files == null || files.Count == 0) return;

            var attached = 0;
            foreach (var file in files)
            {
                if (attached >= 9) break;
                try
                {
                    var copy = await file.CopyAsync(
                        ApplicationData.Current.LocalFolder,
                        "img_" + Guid.NewGuid().ToString("N") + file.FileType,
                        NameCollisionOption.GenerateUniqueName);
                    // Stage for 图文混排: type a caption and press 发送. An empty caption
                    // sends an image-only message, so picking an image never silently drops it.
                    _vm.AttachPendingImage("ms-appdata:///local/" + copy.Name);
                    attached++;
                }
                catch (Exception)
                {
                    // A single unreadable file should not discard the other selected images.
                }
            }
            if (attached == 0)
            {
                _vm.AppendSystem("发送失败：图片读取失败");
                ScrollToBottom();
            }
        }

        private void RemovePendingImage_Click(object sender, RoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            if (!string.IsNullOrEmpty(path)) _vm.RemovePendingImage(path);
        }

        private void ClearPendingImages_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearPendingImages();
        }

        // ---- voice record ----

        private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording) await StartRecordingAsync();
            else await StopRecordingAsync(send: true);
        }

        private async System.Threading.Tasks.Task StartRecordingAsync()
        {
            try
            {
                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                _voiceFile = await ApplicationData.Current.LocalFolder.CreateFileAsync("voice_" + Guid.NewGuid().ToString("N") + ".m4a", CreationCollisionOption.GenerateUniqueName);
                await _mediaCapture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), _voiceFile);

                _isRecording = true;
                _recordStart = DateTimeOffset.Now;
                VoiceGlyph.Glyph = "\uE71A";
                EmojiPanel.Visibility = Visibility.Collapsed;
                RecordingBar.Visibility = Visibility.Visible;
                RecordingHint.Text = "● 录音中… 0″   再次点击麦克风结束并发送";

                _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _recordTimer.Tick += (s, e) =>
                {
                    var sec = (int)(DateTimeOffset.Now - _recordStart).TotalSeconds;
                    RecordingHint.Text = "● 录音中… " + sec + "″   再次点击麦克风结束并发送";
                };
                _recordTimer.Start();
            }
            catch (UnauthorizedAccessException)
            {
                _isRecording = false;
                RecordingBar.Visibility = Visibility.Visible;
                RecordingHint.Text = "麦克风被禁用：设置 > 隐私 > 麦克风，允许应用使用麦克风";
                CleanupCapture();
            }
            catch (Exception ex)
            {
                _isRecording = false;
                RecordingBar.Visibility = Visibility.Visible;
                RecordingHint.Text = "无法录音(0x" + ex.HResult.ToString("X8") + ")：" + ex.Message;
                CleanupCapture();
            }
        }

        private async System.Threading.Tasks.Task StopRecordingAsync(bool send)
        {
            _recordTimer?.Stop();
            _recordTimer = null;
            var seconds = Math.Max(1, (int)(DateTimeOffset.Now - _recordStart).TotalSeconds);
            _isRecording = false;
            VoiceGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
            VoiceGlyph.Glyph = "\uE720";
            RecordingBar.Visibility = Visibility.Collapsed;

            try
            {
                if (_mediaCapture != null) await _mediaCapture.StopRecordAsync();
            }
            catch (Exception) { }

            var file = _voiceFile;
            CleanupCapture();

            if (send && file != null)
            {
                await _vm.SendVoiceAsync("ms-appdata:///local/" + file.Name, seconds);
                ScrollToBottom();
            }
        }

        private void CleanupCapture()
        {
            try { _mediaCapture?.Dispose(); } catch (Exception) { }
            _mediaCapture = null;
            _voiceFile = null;
        }

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                using (var stream = await file.OpenReadAsync())
                {
                    var bytes = new byte[stream.Size];
                    using (var reader = new Windows.Storage.Streams.DataReader(stream))
                    {
                        await reader.LoadAsync((uint)stream.Size);
                        reader.ReadBytes(bytes);
                    }
                    if (_chat is IGatewayService remote)
                    {
                        var msg = await remote.SendFileAsync(_vm.ConversationId, bytes, file.Name);
                        // Cache a local copy so our own outgoing card can open even when
                        // the protocol has no re-download URL (friend offline files).
                        try
                        {
                            msg.LocalFilePath = await CacheOutgoingFileAsync(bytes, file.Name);
                            if (string.IsNullOrEmpty(msg.FileName)) msg.FileName = file.Name;
                            if (string.IsNullOrEmpty(msg.FileSize)) msg.FileSize = FormatBytes(bytes.Length);
                        }
                        catch { /* non-fatal: card still shows, open may fall back */ }
                        _vm.AppendForwarded(msg);
                        ScrollToBottom();
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.AppendSystem("发送文件失败：" + ex.Message);
                ScrollToBottom();
            }
        }

        private async void File_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            e.Handled = true;

            // 1) Prefer a local cache (especially outgoing files we just sent).
            if (!string.IsNullOrEmpty(m.LocalFilePath))
            {
                try
                {
                    var local = await StorageFile.GetFileFromPathAsync(m.LocalFilePath);
                    await Launcher.LaunchFileAsync(local);
                    return;
                }
                catch
                {
                    // fall through to remote / save-as
                }
            }

            // 2) Group file remote download URL via GroupFSDownload.
            var fileId = !string.IsNullOrEmpty(m.FileId) ? m.FileId : null;
            if (_chat is IGatewayService remote
                && !string.IsNullOrEmpty(fileId)
                && !fileId.StartsWith("friend-file:", StringComparison.Ordinal))
            {
                try
                {
                    var url = await remote.GetFileDownloadUrlAsync(_vm.ConversationId, fileId);
                    if (!string.IsNullOrEmpty(url))
                    {
                        // Save into local folder then open, so the card becomes reopenable.
                        var saved = await DownloadRemoteFileAsync(url, m.FileName);
                        if (saved != null)
                        {
                            m.LocalFilePath = saved.Path;
                            await Launcher.LaunchFileAsync(saved);
                            return;
                        }
                        await Launcher.LaunchUriAsync(new Uri(url));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _vm.AppendSystem("获取文件下载链接失败：" + ex.Message);
                    ScrollToBottom();
                    return;
                }
            }

            // 3) Last resort: save-as picker (works for synthetic/local-only cards).
            if (!string.IsNullOrEmpty(m.LocalFilePath) || !string.IsNullOrEmpty(m.FileName))
            {
                try
                {
                    if (!string.IsNullOrEmpty(m.LocalFilePath))
                    {
                        var src = await StorageFile.GetFileFromPathAsync(m.LocalFilePath);
                        var picker = new FileSavePicker
                        {
                            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                            SuggestedFileName = m.FileName ?? src.Name
                        };
                        picker.FileTypeChoices.Add("文件", new List<string> { src.FileType.Length > 0 ? src.FileType : ".bin" });
                        var dest = await picker.PickSaveFileAsync();
                        if (dest != null)
                        {
                            await src.CopyAndReplaceAsync(dest);
                            await Launcher.LaunchFileAsync(dest);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _vm.AppendSystem("保存文件失败：" + ex.Message);
                    ScrollToBottom();
                    return;
                }
            }

            _vm.AppendSystem(string.IsNullOrEmpty(m.FileName)
                ? "无法打开：缺少文件内容或下载链接（好友离线文件暂无远程下载）"
                : "文件：" + m.FileName + (string.IsNullOrEmpty(m.FileSize) ? "" : " (" + m.FileSize + ")"));
            ScrollToBottom();
        }

        private static async Task<string> CacheOutgoingFileAsync(byte[] bytes, string fileName)
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "OutgoingFiles", CreationCollisionOption.OpenIfExists);
            var safe = SanitizeFileName(fileName);
            var stored = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + safe;
            var file = await folder.CreateFileAsync(stored, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, bytes);
            return file.Path;
        }

        private static async Task<StorageFile> DownloadRemoteFileAsync(string url, string preferredName)
        {
            try
            {
                // Try to use the user-selected download folder first
                StorageFolder folder;
                try
                {
                    if (StorageApplicationPermissions.FutureAccessList.ContainsItem("DownloadFolder"))
                        folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync("DownloadFolder");
                    else
                        folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                            "DownloadedFiles", CreationCollisionOption.OpenIfExists);
                }
                catch
                {
                    folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                        "DownloadedFiles", CreationCollisionOption.OpenIfExists);
                }
                var name = SanitizeFileName(string.IsNullOrWhiteSpace(preferredName) ? "download.bin" : preferredName);
                var file = await folder.CreateFileAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + name,
                    CreationCollisionOption.ReplaceExisting);

                using (var http = new System.Net.Http.HttpClient())
                using (var resp = await http.GetAsync(url))
                {
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    await FileIO.WriteBytesAsync(file, bytes);
                }
                return file;
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "file.bin";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
        }

    }
}
