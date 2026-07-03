using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Core;
using Windows.Media.Capture;
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

        public ConversationPage()
        {
            InitializeComponent();
            _chat = App.ChatService;
            _vm = new ConversationViewModel(_chat);
            DataContext = _vm;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

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

            // Only (re)load messages when arriving with a conversation parameter; returning
            // from the background picker (which passes no parameter) keeps the current chat.
            if (e.Parameter is ChatConversation conv)
            {
                await _vm.LoadAsync(conv);
                ScrollToBottom();
            }

            // Mark this conversation active so the list doesn't badge messages read live here.
            App.ActiveConversationId = _vm.ConversationId;

            _chat.MessageReceived += OnMessageReceived;
            _chat.TypingChanged += OnTypingChanged;
        }

        /// <summary>
        /// Apply the message-area background from LocalSettings key "ChatBackground"
        /// per the shared contract:
        ///   missing / ""       -> default MetroBackgroundBrush (plain black)
        ///   starts with "#"    -> solid color "#AARRGGBB"
        ///   starts with "ms-appx" -> image asset (Stretch=UniformToFill)
        /// </summary>
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

            // Put the caret at the end so typing continues naturally.
            InputBox.SelectionStart = InputBox.Text != null ? InputBox.Text.Length : 0;
            InputBox.Focus(FocusState.Programmatic);
        }

        private void GroupInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Conversation != null)
                Frame.Navigate(typeof(GroupInfoPage), _vm.Conversation);
        }

        private void VoiceCallButton_Click(object sender, RoutedEventArgs e)
        {
            var c = _vm.Conversation;
            Frame.Navigate(typeof(VoiceCallPage), new CallArgs
            {
                PeerName = c != null ? c.Title : _vm.Title,
                PeerAvatar = c != null ? c.AvatarPath : null,
                IsVideo = false
            });
        }

        private void VideoCallButton_Click(object sender, RoutedEventArgs e)
        {
            var c = _vm.Conversation;
            Frame.Navigate(typeof(VideoCallPage), new CallArgs
            {
                PeerName = c != null ? c.Title : _vm.Title,
                PeerAvatar = c != null ? c.AvatarPath : null,
                IsVideo = true
            });
        }

        // ---- chat background picker ----

        private void ChatBgButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate without a parameter so OnNavigatedTo keeps the current chat and
            // simply re-applies "ChatBackground" when we come back from the picker.
            Frame.Navigate(typeof(QQReborn.App.Views.ChatBackgroundPage));
        }

        // ---- load earlier (fake) history ----

        private async void LoadEarlierStrip_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            // Remember the first real message so we can keep it on screen after prepending.
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

        private void Bubble_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            if (!m.IsIncoming) return;
            var who = string.IsNullOrEmpty(m.SenderName) ? "对方" : m.SenderName;
            _vm.AppendSystem("你戳了戳 " + who);
            ScrollToBottom();
            e.Handled = true;
        }

        // ---- emoji / sticker panel ----

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
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

            var copy = await file.CopyAsync(ApplicationData.Current.LocalFolder, "img_" + Guid.NewGuid().ToString("N") + file.FileType, NameCollisionOption.GenerateUniqueName);
            await _vm.SendImageAsync("ms-appdata:///local/" + copy.Name);
            ScrollToBottom();
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
                VoiceGlyph.Text = "⏹";
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
            VoiceGlyph.Text = "🎤";
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

        // ---- voice playback ----

        private void Voice_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m) || string.IsNullOrEmpty(m.AudioPath)) return;
            try
            {
                if (_player == null) _player = new Windows.Media.Playback.MediaPlayer();
                _player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(m.AudioPath));
                _player.Play();
            }
            catch (Exception) { }
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

            // 回应 (reactions submenu)
            var react = new MenuFlyoutSubItem { Text = "回应" };
            foreach (var emoji in new[] { "👍", "❤️", "😂" })
            {
                var captured = emoji;
                var item = new MenuFlyoutItem { Text = captured };
                item.Click += (s, e) => m.ToggleReaction(captured);
                react.Items.Add(item);
            }
            menu.Items.Add(react);

            // 转发
            var forward = new MenuFlyoutItem { Text = "转发" };
            forward.Click += async (s, e) => await ShowForwardPickerAsync(m);
            menu.Items.Add(forward);

            // 撤回 (outgoing text only)
            if (m.IsOutgoing && m.IsText)
            {
                var recall = new MenuFlyoutItem { Text = "撤回" };
                recall.Click += (s, e) => _vm.RecallMessage(m);
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
            var conversations = await _chat.GetConversationsAsync();

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

            var text = m.IsText ? m.Text : ForwardSummary(m);
            var sent = await _chat.SendTextAsync(target.Id, text ?? string.Empty);

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
