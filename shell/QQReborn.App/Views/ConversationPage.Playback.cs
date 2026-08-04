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
        // ---- voice playback ----

        private bool _resolvingVoice;

        private async void Voice_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            e.Handled = true;
            if (_resolvingVoice) return;

            _resolvingVoice = true;
            try
            {
                // 1) Prefer local/original audio path first.
                // Our own outgoing voice keeps ms-appdata path; re-download from QQ CDN
                // for self-sent PTT often fails (illegal params) and used to crash via
                // unhandled RequestAsync exceptions in this async void handler.
                if (await TryPlayLocalOrUriAsync(m.AudioPath))
                    return;

                // 2) Ask server for playable bytes (WAV preferred).
                if (_chat is IGatewayService remoteVoice)
                {
                    try
                    {
                        var res = await remoteVoice.GetVoicePlayableAsync(m.Id);
                        if (res != null && res.Bytes != null && res.Bytes.Length > 0)
                        {
                            string ext = ".audio";
                            switch (res.Format)
                            {
                                case "mp3": ext = ".mp3"; break;
                                case "amr": ext = ".amr"; break;
                                case "ogg": ext = ".ogg"; break;
                                case "wav": ext = ".wav"; break;
                                case "silk": ext = ".silk"; break;
                            }
                            var safeId = (m.Id ?? "voice").Replace(':', '_').Replace('/', '_').Replace('\\', '_');
                            var folder = ApplicationData.Current.TemporaryFolder;
                            var file = await folder.CreateFileAsync("voice_" + safeId + ext, CreationCollisionOption.ReplaceExisting);
                            await FileIO.WriteBytesAsync(file, res.Bytes);

                            try
                            {
                                EnsurePlayer();
                                _player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                                _player.Play();
                                // Keep local path so a second tap is instant / offline-safe.
                                m.AudioPath = file.Path;
                                return;
                            }
                            catch (Exception)
                            {
                                _vm.AppendSystem(res.Format == "silk" || res.Format == "unknown"
                                    ? "语音播放失败（SILK 需服务器安装 silk_v3_decoder）"
                                    : "语音播放失败");
                                ScrollToBottom();
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never rethrow from async void: that hard-crashes the UWP process.
                        System.Diagnostics.Debug.WriteLine("GetVoicePlayableAsync failed: " + ex);
                    }

                    // 3) Last remote fallback: media URL (may still be SILK/unplayable).
                    try
                    {
                        var path = await remoteVoice.GetMediaUrlAsync(m.Id);
                        if (!string.IsNullOrEmpty(path))
                        {
                            m.AudioPath = path;
                            if (await TryPlayLocalOrUriAsync(path))
                                return;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("GetMediaUrlAsync(voice) failed: " + ex);
                    }
                }

                _vm.AppendSystem("语音加载失败（可能是格式不支持或服务端无法解码）");
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                // Hard guard: Voice_Tapped is async void; any escape kills the app.
                _vm.AppendSystem("语音播放异常：" + (string.IsNullOrEmpty(ex.Message) ? "未知错误" : ex.Message));
                ScrollToBottom();
            }
            finally
            {
                _resolvingVoice = false;
            }
        }

        private async void Forward_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage message)) return;
            try
            {
                if (message.ForwardEntries.Count == 0 && _chat is IGatewayService remote)
                {
                    var details = await remote.GetForwardDetailsAsync(message.Id);
                    foreach (var item in details) message.ForwardEntries.Add(item);
                }
                var text = message.ForwardEntries.Count == 0
                    ? (message.Text ?? "没有可显示的转发内容")
                    : string.Join("\n", message.ForwardEntries.Select(x => x.DisplayText));
                await new MessageDialog(text, "合并转发").ShowAsync();
            }
            catch (Exception ex)
            {
                await new MessageDialog("无法读取转发内容：" + ex.Message, "合并转发").ShowAsync();
            }
        }

        private void EnsurePlayer()
        {
            if (_player != null) return;
            _player = new Windows.Media.Playback.MediaPlayer();
            // Avoid system media transport / focus quirks causing unexpected failures.
            try { _player.CommandManager.IsEnabled = false; } catch { }
            try { _player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media; } catch { }
        }

        private async Task<bool> TryPlayLocalOrUriAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                EnsurePlayer();

                // Local absolute path from TemporaryFolder / LocalFolder cache.
                if (path.IndexOf(":\\", StringComparison.Ordinal) >= 0 || path.StartsWith("\\\\", StringComparison.Ordinal))
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    _player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                    _player.Play();
                    return true;
                }

                // ms-appdata:///local/xxx written by the recorder.
                if (path.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    _player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
                    _player.Play();
                    return true;
                }

                // Bare relative local file name under LocalFolder.
                try
                {
                    var local = await ApplicationData.Current.LocalFolder.GetFileAsync(path);
                    _player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(local);
                    _player.Play();
                    return true;
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TryPlayLocalOrUriAsync failed: " + ex);
                return false;
            }
        }

        // ---- image full-screen viewer ----

        // Guards against double-taps stacking multiple getMediaUrl round-trips / navigations.
        private bool _resolvingImage;

        private async void MixedImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Image inside a multi-element (text+image) bubble: DataContext is MessageElement,
            // not ChatMessage. Resolve owning message via ItemsControl parent chain.
            e.Handled = true;
            if (_resolvingImage) return;
            var img = sender as FrameworkElement;
            var el = img?.DataContext as MessageElement;
            if (el == null || !el.IsImage) return;

            ChatMessage owner = null;
            DependencyObject d = img;
            while (d != null)
            {
                d = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
                var fe = d as FrameworkElement;
                if (fe?.DataContext is ChatMessage cm)
                {
                    owner = cm;
                    break;
                }
            }
            if (owner == null) return;

            _resolvingImage = true;
            try
            {
                var path = el.Url;
                if (string.IsNullOrEmpty(path) && _chat is IGatewayService remote)
                {
                    try { path = await remote.GetMediaUrlAsync(owner.Id); }
                    catch { path = null; }
                    if (!string.IsNullOrEmpty(path)) el.Url = path;
                }
                if (string.IsNullOrEmpty(path))
                {
                    _vm.AppendSystem("图片加载失败");
                    ScrollToBottom();
                    return;
                }

                // Gallery: all image elements across mixed messages + pure image messages.
                var items = new List<ImageGalleryItem>();
                var startIndex = 0;
                foreach (var msg in _vm.Messages)
                {
                    if (msg.HasElements && msg.Elements != null)
                    {
                        foreach (var part in msg.Elements)
                        {
                            if (part == null || !part.IsImage) continue;
                            if (part == el) startIndex = items.Count;
                            items.Add(new ImageGalleryItem
                            {
                                MessageId = msg.Id,
                                Path = part.Url
                            });
                        }
                    }
                    else if (msg.IsImage)
                    {
                        items.Add(new ImageGalleryItem { MessageId = msg.Id, Path = msg.ImagePath });
                    }
                }
                if (items.Count == 0)
                {
                    items.Add(new ImageGalleryItem { MessageId = owner.Id, Path = path });
                }

                Frame.Navigate(typeof(ImageViewerPage), new ImageGalleryArgs
                {
                    Items = items,
                    Index = startIndex
                });
            }
            finally
            {
                _resolvingImage = false;
            }
        }

        private async void Image_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            e.Handled = true;
            if (_resolvingImage) return;

            _resolvingImage = true;
            try
            {
                // Build a gallery of every image bubble in this conversation so the viewer
                // can swipe left/right (real QQ behaviour). Stickers are excluded — they're
                // small emoticons, not a photo album.
                var items = new List<ImageGalleryItem>();
                var startIndex = 0;
                foreach (var msg in _vm.Messages)
                {
                    if (!msg.IsImage) continue;
                    if (msg.Id == m.Id) startIndex = items.Count;
                    items.Add(new ImageGalleryItem
                    {
                        MessageId = msg.Id,
                        Path = msg.ImagePath
                    });
                }
                if (items.Count == 0)
                {
                    // Fallback: tapped message only (shouldn't happen if IsImage).
                    items.Add(new ImageGalleryItem { MessageId = m.Id, Path = m.ImagePath });
                }

                // Resolve the tapped item first so open feels instant when possible.
                var tapped = items[startIndex];
                if (string.IsNullOrEmpty(tapped.Path) && _chat is IGatewayService remoteImg)
                {
                    try { tapped.Path = await remoteImg.GetMediaUrlAsync(m.Id); }
                    catch (Exception) { /* leave empty; viewer retries */ }
                    if (!string.IsNullOrEmpty(tapped.Path))
                        m.ImagePath = tapped.Path;
                }

                if (items.Count == 1 && string.IsNullOrEmpty(items[0].Path))
                {
                    _vm.AppendSystem("图片加载失败");
                    ScrollToBottom();
                    return;
                }

                Frame.Navigate(typeof(ImageViewerPage), new ImageGalleryArgs
                {
                    Items = items,
                    Index = startIndex
                });
            }
            finally
            {
                _resolvingImage = false;
            }
        }

        // ---- video playback ----

        // Guards against a double-tap firing two overlapping GetMediaUrlAsync round-trips
        // for the same (or different) video bubbles while the first resolve is in flight.
        private bool _resolvingVideo;

        private async void Video_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            e.Handled = true;
            if (_resolvingVideo) return;

            // Mock backend never produces Video messages, so there's nothing to resolve --
            // this path only makes sense against the remote backend (see RemoteChatService.GetMediaUrlAsync).
            if (!(_chat is IGatewayService remote)) return;

            _resolvingVideo = true;
            try
            {
                string url;
                try
                {
                    url = await remote.GetMediaUrlAsync(m.Id);
                }
                catch (Exception)
                {
                    url = null;
                }
                if (string.IsNullOrEmpty(url))
                {
                    _vm.AppendSystem("视频加载失败");
                    ScrollToBottom();
                    return;
                }
                Frame.Navigate(typeof(VideoPlayerPage), url);
            }
            finally
            {
                _resolvingVideo = false;
            }
        }

    }
}
