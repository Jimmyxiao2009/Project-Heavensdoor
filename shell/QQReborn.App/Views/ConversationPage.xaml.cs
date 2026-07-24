using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
// ImageGalleryArgs / ImageGalleryItem live in Models
using Windows.Media.Capture;
using Windows.Storage.AccessCache;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ConversationPage : Page
    {
        private readonly ConversationViewModel _vm;
        private readonly IChatService _chat;

        private MediaCapture _mediaCapture;
        private StorageFile _voiceFile;
        private bool _isRecording;
        private DateTimeOffset _recordStart;
        private DispatcherTimer _recordTimer;
        private Windows.Media.Playback.MediaPlayer _player;

        // Persisted "回车发送" preference (LocalSettings key shared with MockProfileService).
        // Default true so behavior matches the historical send-on-Enter unless toggled off.
        private const string EnterToSendSettingKey = "qqr.settings.enterToSend";
        private bool _enterToSend = true;

        // Set when OnNavigatedFrom fires while a LoadAsync started by OnNavigatedTo is still
        // in flight (remote backend, up to ~10s). The OnNavigatedTo continuation checks this
        // after the await and skips any further UI work on what is now a dead page -- see the
        // OnNavigatedTo/OnNavigatedFrom race explained below.
        private bool _hasLeft;

        public ConversationPage()
        {
            InitializeComponent();
            _chat = App.ChatService;
            _vm = new ConversationViewModel(_chat);
            DataContext = _vm;

            // Keep this page instance (and the VM's in-memory Messages/draft/reply-target/
            // scroll position) alive across GoBack from ChatBackgroundPage/GroupInfoPage/
            // VoiceCallPage/VideoCallPage instead of Frame recreating a blank one. UWP replays
            // the original navigation Parameter on GoBack, so OnNavigatedTo below guards
            // against redundantly reloading the same ChatConversation we already have.
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _hasLeft = false; // this instance is on screen again (fresh nav or cached reuse)

            // Re-read the chat background every time so returning from the picker applies it.
            ApplyChatBackground();

            // Re-read the "回车发送" preference each time so toggling it in Settings
            // takes effect on the next visit without an app restart.
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[EnterToSendSettingKey];
                _enterToSend = !(raw is bool b) || b; // default true when unset/other type
            }
            catch
            {
                _enterToSend = true;
            }

            // Skip the (re)load ONLY for a Back navigation to the conversation we already have
            // loaded: with NavigationCacheMode Enabled, GoBack from GroupInfoPage/VoiceCallPage/
            // etc. replays the ORIGINAL ChatConversation parameter, which used to force a full,
            // state-destroying reload (draft/reply-banner/scroll position all lost) every time.
            // A FORWARD navigation (NavigationMode.New from the main list) must always reload
            // even for the same conversation id, because while the user sat on MainPage this
            // cached page was unsubscribed (OnNavigatedFrom detached the handlers) -- messages
            // that arrived meanwhile are neither in Messages nor coming via push, and an unread
            // badge is exactly why the user is tapping back in. Returning from the background
            // picker (no parameter at all) keeps the current chat as before.
            // Known, accepted gap: a message that arrives while the user is parked on a
            // sub-page (background picker / group info / call) won't show after GoBack since
            // the reload is skipped; it appears with the next push or the next fresh entry.
            // That small window is the cost of preserving the in-progress state.
            var conv = e.Parameter as ChatConversation;
            bool needLoad = conv != null
                && (e.NavigationMode != NavigationMode.Back || conv.Id != _vm.ConversationId || _vm.Conversation == null);

            // Subscribe and mark this conversation active BEFORE awaiting LoadAsync, not after.
            // LoadAsync's GetMessagesAsync can take up to ~10s against a remote backend; the
            // old code subscribed only once that await returned, which meant:
            //  - any push for this conversation that arrived during the load was silently lost
            //    (nobody was listening yet), and
            //  - if the user hit Back while the load was still running, OnNavigatedFrom's
            //    "-=" was a no-op against handlers that weren't attached yet, and this
            //    continuation would then attach them anyway once the load finished -- leaking
            //    this dead page+VM forever on the IChatService singleton's events, and pinning
            //    App.ActiveConversationId to a conversation nobody is looking at (which
            //    permanently suppresses that conversation's unread badge on the main list).
            // Subscribing first means OnNavigatedFrom's unsubscribe (fired from Back while we
            // await below) is always a correct symmetric match. ConversationViewModel.LoadAsync
            // merges its snapshot fetch with anything Append()'ed live during the await instead
            // of clobbering it, so a message that races in during the load is kept exactly once.
            App.ActiveConversationId = conv != null ? conv.Id : _vm.ConversationId;
            _chat.MessageReceived += OnMessageReceived;
            _chat.TypingChanged += OnTypingChanged;

            if (needLoad)
            {
                // Do not hold navigation on the remote transcript request. LoadAsync first
                // reads MessageCache, paints it, then refreshes from the server in this
                // background task. The page remains interactive while the network is slow.
                _ = LoadConversationInBackgroundAsync(conv);
            }

            // Opening (or re-focusing) a chat clears the local badge immediately; cloud read
            // acknowledgement is best-effort and must not delay page entry.
            if (!string.IsNullOrEmpty(App.ActiveConversationId))
            {
                UnreadBadgeStore.Clear(App.ActiveConversationId);
                if (_chat is RemoteChatService remoteRead)
                    _ = MarkConversationReadInBackgroundAsync(remoteRead, App.ActiveConversationId);
            }
        }

        /// <summary>
        /// Apply the message-area background from LocalSettings key "ChatBackground"
        /// per the shared contract:
        ///   missing / ""       -> default MetroBackgroundBrush (plain black)
        ///   starts with "#"    -> solid color "#AARRGGBB"
        ///   starts with "ms-appx" -> image asset (Stretch=UniformToFill)
        /// </summary>
        private async System.Threading.Tasks.Task LoadConversationInBackgroundAsync(ChatConversation conv)
        {
            try
            {
                await _vm.LoadAsync(conv);
                if (!_hasLeft) ScrollToBottom();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Conversation background load failed: " + ex);
            }
        }

        private static async System.Threading.Tasks.Task MarkConversationReadInBackgroundAsync(RemoteChatService remote, string conversationId)
        {
            try { await remote.MarkConversationReadAsync(conversationId); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Mark read failed: " + ex); }
        }

        private void ApplyChatBackground()
        {
            Brush brush = (Brush)Application.Current.Resources["MetroBackgroundBrush"];
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values["ChatBackground"] as string;
                if (!string.IsNullOrEmpty(raw))
                {
                    if (raw[0] == '#')
                    {
                        brush = new SolidColorBrush(ParseColor(raw));
                    }
                    else if (raw.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase))
                    {
                        brush = new ImageBrush
                        {
                            ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(raw)),
                            Stretch = Stretch.UniformToFill
                        };
                    }
                }
            }
            catch (Exception)
            {
                // Bad value -> fall back to the default brush.
                brush = (Brush)Application.Current.Resources["MetroBackgroundBrush"];
            }
            MessageArea.Background = brush;
        }

        /// <summary>Parse a "#AARRGGBB" hex string into a Color.</summary>
        private static Windows.UI.Color ParseColor(string hex)
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6) s = "FF" + s; // tolerate "#RRGGBB"
            byte a = Convert.ToByte(s.Substring(0, 2), 16);
            byte r = Convert.ToByte(s.Substring(2, 2), 16);
            byte g = Convert.ToByte(s.Substring(4, 2), 16);
            byte b = Convert.ToByte(s.Substring(6, 2), 16);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Tell a LoadAsync that's still in flight (if any) not to touch this page's UI
            // once it completes -- see the subscribe-before-await comment in OnNavigatedTo.
            _hasLeft = true;

            // No conversation is on screen anymore; only clear if it's still ours so we
            // don't stomp a value another page may have set.
            if (App.ActiveConversationId == _vm.ConversationId) App.ActiveConversationId = null;

            _chat.MessageReceived -= OnMessageReceived;
            _chat.TypingChanged -= OnTypingChanged;
            if (_isRecording) await StopRecordingAsync(send: false);
            if (_player != null) { _player.Dispose(); _player = null; }
            base.OnNavigatedFrom(e);
        }

        private async void OnMessageReceived(object sender, ChatMessage msg)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _vm.OnIncoming(msg);
                ScrollToBottom();
            });

            // Live messages for the open chat: keep badges at 0 for messages already seen.
            if (msg != null
                && msg.ConversationId == _vm.ConversationId
                && msg.Direction != MessageDirection.Outgoing)
            {
                UnreadBadgeStore.Clear(msg.ConversationId);
                if (_chat is RemoteChatService remoteLive)
                {
                    try { await remoteLive.MarkConversationReadAsync(msg.ConversationId); }
                    catch { /* best-effort */ }
                }
            }
        }

        private async void OnTypingChanged(object sender, TypingState state)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (state.ConversationId == _vm.ConversationId) _vm.IsPeerTyping = state.IsTyping;
            });
        }

        private void ScrollToBottom()
        {
            if (_vm.Messages.Count > 0) MessageList.ScrollIntoView(_vm.Messages[_vm.Messages.Count - 1]);
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await _vm.SendAsync();
            ScrollToBottom();
        }

        private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                // When 回车发送 is off, Enter inserts a newline (AcceptsReturn handles it),
                // so don't send. When on, plain Enter sends and Shift+Enter inserts a newline.
                if (!_enterToSend) return;

                var shift = (CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                if (!shift)
                {
                    e.Handled = true;
                    await _vm.SendAsync();
                    ScrollToBottom();
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        // ---- group @mention ----

        private async void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_vm.IsGroup) return;
            if (_mentionFlyoutOpen) return;

            var text = InputBox.Text ?? string.Empty;
            // Trigger only when the user just typed a trailing "@" at the caret end.
            if (text.Length == 0 || text[text.Length - 1] != '@') return;
            if (InputBox.SelectionStart != text.Length) return;

            await ShowMentionPickerAsync();
        }

        private bool _mentionFlyoutOpen;

        private async System.Threading.Tasks.Task ShowMentionPickerAsync()
        {
            IReadOnlyList<GroupMember> members;
            try
            {
                members = await _chat.GetGroupMembersAsync(_vm.ConversationId);
            }
            catch (Exception)
            {
                return;
            }
            if (members == null || members.Count == 0) return;

            var menu = new MenuFlyout { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
            foreach (var member in members)
            {
                var captured = member;
                var item = new MenuFlyoutItem { Text = string.IsNullOrEmpty(captured.Name) ? "群友" : captured.Name };
                item.Click += (s, args) => InsertMention(captured);
                menu.Items.Add(item);
            }

            _mentionFlyoutOpen = true;
            menu.Closed += (s, args) => _mentionFlyoutOpen = false;
            menu.ShowAt(InputBox);
        }

        private void InsertMention(GroupMember member)
        {
            var name = string.IsNullOrEmpty(member.Name) ? "群友" : member.Name;
            var text = _vm.Draft ?? string.Empty;
            // Replace the trailing "@" that opened the picker with "@昵称 ".
            if (text.Length > 0 && text[text.Length - 1] == '@')
                text = text.Substring(0, text.Length - 1);
            _vm.Draft = text + "@" + name + " ";
            _vm.PendingMentions.Add(new ViewModels.ConversationViewModel.MentionInfo { Uin = member.Uin, Display = name });

            // Put the caret at the end so typing continues naturally.
            InputBox.SelectionStart = InputBox.Text != null ? InputBox.Text.Length : 0;
            InputBox.Focus(FocusState.Programmatic);
        }

        private void GroupInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Conversation != null)
                Frame.Navigate(typeof(GroupInfoPage), _vm.Conversation);
        }

        private void MessageArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // The message list stretches to fill the middle area. A tap on its unused
            // lower area is a deliberate lightweight way back to the conversation list;
            // taps inside a message item are left for that item's own handlers.
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is ListViewItem) return;
                source = VisualTreeHelper.GetParent(source);
            }

            e.Handled = true;
            if (Frame.CanGoBack) Frame.GoBack();
        }

        // ---- chat background picker ----

        private void ChatBgButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate without a parameter so OnNavigatedTo keeps the current chat and
            // simply re-applies "ChatBackground" when we come back from the picker.
            Frame.Navigate(typeof(QQReborn.App.Views.ChatBackgroundPage));
        }

        // ---- load earlier history (fabricated for the mock backend, real for remote --
        // see ConversationViewModel.LoadEarlierAsync) ----

        private async void LoadEarlierStrip_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            // Remember the item that was first on screen so we can keep it in view after
            // LoadEarlierAsync prepends older messages ahead of it -- both the mock batch and
            // the real remote page insert at index 0 and never touch/remove this item, they
            // only shift its index up, so it's always still in Messages afterwards.
            // ScrollIntoView is a best-effort "restore roughly where the user was reading" --
            // it snaps the anchor item to whatever position the ListView chooses (not
            // necessarily pixel-identical to before the insert), but that's the only scroll-
            // position primitive this ListView exposes (no ViewChanged-based offset capture
            // is wired up here), and it's enough to avoid the jarring jump to the very top
            // that a bare Messages.Insert(0, ...) would otherwise cause.
            var anchor = _vm.Messages.Count > 0 ? _vm.Messages[0] : null;
            await _vm.LoadEarlierAsync();
            if (anchor != null) MessageList.ScrollIntoView(anchor);
        }

        private void ClearReplyButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearReplyTarget();
        }

        // ---- 发送位置 ----

        private async void LocationButton_Click(object sender, RoutedEventArgs e)
        {
            string address;
            try
            {
                var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
                if (access == Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                {
                    var geo = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 100 };
                    var pos = await geo.GetGeopositionAsync();
                    var p = pos.Coordinate.Point.Position;
                    address = "纬度 " + p.Latitude.ToString("F5") + ", 经度 " + p.Longitude.ToString("F5");
                }
                else
                {
                    address = "深圳市南山区 (未授权定位，演示位置)";
                }
            }
            catch (Exception)
            {
                address = "深圳市南山区 (定位不可用，演示位置)";
            }

            await _vm.SendLocationAsync("我的位置", address, "ms-appx:///Assets/Wide310x150Logo.scale-200.png");
            ScrollToBottom();
        }

        // ---- 戳一戳 (double tap an incoming bubble) ----

        private async void Bubble_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            if (!m.IsIncoming) return;
            e.Handled = true;

            var who = string.IsNullOrEmpty(m.SenderName) ? "对方" : m.SenderName;

            // Against a remote backend, actually send the nudge on the wire before showing
            // the local notice -- friend chats target the peer, group chats target whoever
            // sent the bubble that was double-tapped (m.SenderUin is that sender's uin in
            // both cases: for a friend conversation it's always the peer, since only the
            // peer can be the sender of an incoming message; see BotSessionManager.SendNudgeAsync).
            if (_chat is RemoteChatService remote)
            {
                bool sent;
                try
                {
                    sent = await remote.SendNudgeAsync(_vm.ConversationId, m.SenderUin);
                }
                catch (Exception)
                {
                    sent = false;
                }
                if (!sent)
                {
                    _vm.AppendSystem("戳一戳发送失败");
                    ScrollToBottom();
                    return;
                }
            }

            _vm.AppendSystem("你戳了戳 " + who);
            ScrollToBottom();
        }

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
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            StorageFile copy;
            try
            {
                copy = await file.CopyAsync(ApplicationData.Current.LocalFolder, "img_" + Guid.NewGuid().ToString("N") + file.FileType, NameCollisionOption.GenerateUniqueName);
            }
            catch (Exception)
            {
                // Local file-system failure copying the picked image into LocalFolder --
                // nothing was sent, so just tell the user rather than letting this async void
                // handler's exception escape and kill the app.
                _vm.AppendSystem("发送失败：图片读取失败");
                ScrollToBottom();
                return;
            }
            // Stage for 图文混排: type caption then press 发送 (or send image-only with empty draft).
            _vm.AttachPendingImage("ms-appdata:///local/" + copy.Name);
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
                    if (_chat is RemoteChatService remote)
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
            if (_chat is RemoteChatService remote
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
                if (_chat is RemoteChatService remoteVoice)
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
                if (string.IsNullOrEmpty(path) && _chat is RemoteChatService remote)
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
                if (string.IsNullOrEmpty(tapped.Path) && _chat is RemoteChatService remoteImg)
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
            if (!(_chat is RemoteChatService remote)) return;

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

        // ---- long-press menu ----

        private void Bubble_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            ShowMessageMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Bubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Touch/pen long-press raises both Holding (Started) and, on release, RightTapped
            // for the same gesture -- without this guard the menu popped up twice. Only mouse
            // right-click reaches here; Holding above owns touch/pen.
            if (e.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Mouse) return;
            ShowMessageMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowMessageMenu(FrameworkElement anchor)
        {
            if (!(anchor?.DataContext is ChatMessage m)) return;
            if (m.IsSystem) return;

            var menu = new MenuFlyout();

            if (m.IsText)
            {
                var copy = new MenuFlyoutItem { Text = "复制" };
                copy.Click += (s, e) =>
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText(m.Text ?? string.Empty);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                };
                menu.Items.Add(copy);
            }

            // 回复
            var reply = new MenuFlyoutItem { Text = "回复" };
            reply.Click += (s, e) => _vm.ReplyTarget = m;
            menu.Items.Add(reply);

            // 群消息回应（Lagrange SetGroupReaction）
            if (_vm.IsGroup && _chat is RemoteChatService)
            {
                var react = new MenuFlyoutSubItem { Text = "回应" };
                foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" })
                {
                    var captured = emoji;
                    var item = new MenuFlyoutItem { Text = captured };
                    item.Click += async (s, e) =>
                    {
                        var remote = _chat as RemoteChatService;
                        if (remote == null) return;
                        var adding = !m.Reactions.Contains(captured);
                        try
                        {
                            var ok = await remote.SetGroupReactionAsync(_vm.ConversationId, m.Id, captured, adding);
                            if (ok) m.ToggleReaction(captured);
                            else
                            {
                                _vm.AppendSystem("回应失败");
                                ScrollToBottom();
                            }
                        }
                        catch (Exception ex)
                        {
                            _vm.AppendSystem("回应失败：" + ex.Message);
                            ScrollToBottom();
                        }
                    };
                    react.Items.Add(item);
                }
                menu.Items.Add(react);
            }

            // 转发（服务端 MessageBuilder.MultiMsg）
            var forward = new MenuFlyoutItem { Text = "转发" };
            forward.Click += async (s, e) => await ShowForwardPickerAsync(m);
            menu.Items.Add(forward);

            // 撤回 (outgoing text only). Against a remote backend the actual recall is a
            // server round-trip (RecallMessageAsync) -- this lambda is itself the top of an
            // async void call chain (MenuFlyoutItem.Click), so RecallMessageAsync's own
            // try/catch is what stands between a backend failure and a crashed app; nothing
            // else guards this call site.
            if (m.IsOutgoing && m.IsText)
            {
                var recall = new MenuFlyoutItem { Text = "撤回" };
                recall.Click += async (s, e) => await _vm.RecallMessageAsync(m);
                menu.Items.Add(recall);
            }

            var del = new MenuFlyoutItem { Text = "删除" };
            del.Click += (s, e) => _vm.DeleteMessage(m);
            menu.Items.Add(del);

            menu.ShowAt(anchor);
        }

        // ---- forward: pick a target conversation ----

        private async System.Threading.Tasks.Task ShowForwardPickerAsync(ChatMessage m)
        {
            System.Collections.Generic.IReadOnlyList<ChatConversation> conversations;
            try
            {
                conversations = await _chat.GetConversationsAsync();
            }
            catch (Exception)
            {
                _vm.AppendSystem("转发失败：服务器未连接");
                ScrollToBottom();
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 360
            };
            foreach (var c in conversations) list.Items.Add(c);
            list.DisplayMemberPath = "Title";

            var dialog = new ContentDialog
            {
                Title = "转发到",
                Content = list,
                PrimaryButtonText = "发送",
                SecondaryButtonText = "取消"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            if (!(list.SelectedItem is ChatConversation target)) return;

            ChatMessage sent;
            try
            {
                sent = await _chat.ForwardMessageAsync(target.Id, m.Id);
            }
            catch (Exception)
            {
                _vm.AppendSystem("转发失败：" + target.Title + " 未收到消息");
                ScrollToBottom();
                return;
            }

            if (target.Id == _vm.ConversationId)
            {
                _vm.AppendForwarded(sent);
                ScrollToBottom();
            }
        }

        private static string ForwardSummary(ChatMessage m)
        {
            if (m.IsImage) return "[图片]";
            if (m.IsSticker) return "[表情]";
            if (m.IsVoice) return "[语音]";
            if (m.IsLinkCard) return m.LinkTitle ?? "[链接]";
            if (m.IsFile) return "[文件] " + (m.FileName ?? string.Empty);
            if (m.IsLocation) return "[位置] " + (m.PlaceName ?? string.Empty);
            return m.Text ?? string.Empty;
        }
    }
}
