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
        // A busy group can deliver several messageReceived frames in one dispatcher turn.
        // Coalesce the follow-up ScrollIntoView calls so each frame is appended once without
        // forcing the ListView to measure and reposition repeatedly.
        private bool _scrollQueued;

        private bool _multiSelectMode;

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
            _scrollQueued = false;

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
            if (conv != null) App.RememberConversation(conv);
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
            // Symmetric -= before += so a re-entered OnNavigatedTo never doubles handlers.
            _chat.MessageReceived -= OnMessageReceived;
            _chat.MessageReceived += OnMessageReceived;
            _chat.TypingChanged -= OnTypingChanged;
            _chat.TypingChanged += OnTypingChanged;
            if (_chat is IGatewayService remoteRecall)
            {
                remoteRecall.MessageRecalled -= OnMessageRecalled;
                remoteRecall.MessageRecalled += OnMessageRecalled;
            }

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
                if (_chat is IGatewayService remoteRead)
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
        private bool _selfIsGroupAdmin;
        private long _selfUin;

        private async System.Threading.Tasks.Task LoadConversationInBackgroundAsync(ChatConversation conv)
        {
            try
            {
                await _vm.LoadAsync(conv);
                if (!_hasLeft) ScrollToBottom();
                if (conv != null && conv.Kind == ConversationKind.Group)
                    _ = ResolveSelfGroupRoleAsync(conv.Id);
                else
                    _selfIsGroupAdmin = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Conversation background load failed: " + ex);
            }
        }

        private async System.Threading.Tasks.Task ResolveSelfGroupRoleAsync(string conversationId)
        {
            try
            {
                if (_selfUin <= 0)
                {
                    var self = await _chat.GetSelfAsync();
                    _selfUin = self != null ? self.Uin : 0;
                }
                if (_selfUin <= 0 || string.IsNullOrEmpty(conversationId))
                {
                    _selfIsGroupAdmin = false;
                    return;
                }
                var members = await _chat.GetGroupMembersAsync(conversationId);
                var me = members != null
                    ? members.FirstOrDefault(m => m != null && m.Uin == _selfUin)
                    : null;
                _selfIsGroupAdmin = me != null && me.IsAdmin;
            }
            catch (Exception ex)
            {
                _selfIsGroupAdmin = false;
                System.Diagnostics.Debug.WriteLine("ResolveSelfGroupRole: " + ex.Message);
            }
        }

        private static async System.Threading.Tasks.Task MarkConversationReadInBackgroundAsync(IGatewayService remote, string conversationId)
        {
            try { await remote.MarkConversationReadAsync(conversationId, System.DateTimeOffset.UtcNow.ToString("o")); }
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
            if (_chat is IGatewayService remoteRecall)
                remoteRecall.MessageRecalled -= OnMessageRecalled;
            ExitMultiSelectMode();
            if (_isRecording) await StopRecordingAsync(send: false);
            if (_player != null) { _player.Dispose(); _player = null; }
            base.OnNavigatedFrom(e);
        }

        private async void OnMessageReceived(object sender, ChatMessage msg)
        {
            if (msg == null || msg.ConversationId != _vm.ConversationId) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_hasLeft) return;
                _vm.OnIncoming(msg);
                QueueScrollToBottom();
            });

            // Live messages for the open chat: keep badges at 0 for messages already seen.
            if (msg != null
                && msg.ConversationId == _vm.ConversationId
                && msg.Direction != MessageDirection.Outgoing)
            {
                UnreadBadgeStore.Clear(msg.ConversationId);
                if (_chat is IGatewayService remoteLive)
                {
                    try { await remoteLive.MarkConversationReadAsync(msg.ConversationId, System.DateTimeOffset.UtcNow.ToString("o")); }
                    catch { /* best-effort */ }
                }
            }
        }

        private async void OnMessageRecalled(object sender, MessageRecalledInfo info)
        {
            if (info == null) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (info.ConversationId != _vm.ConversationId) return;
                _vm.ApplyPeerRecall(info.MessageId, info.NapCatMessageId, info.SenderName, info.Preview);
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

        private void QueueScrollToBottom()
        {
            if (_hasLeft || _scrollQueued) return;
            _scrollQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                _scrollQueued = false;
                if (!_hasLeft) ScrollToBottom();
            });
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await TrySendAsync();
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
                    await TrySendAsync();
                }
            }
        }

        private async Task TrySendAsync()
        {
            if (!_vm.CanSend) return;
            if (UtilitySettings.ConfirmBeforeSend)
            {
                var dlg = new MessageDialog("确认发送这条消息？", "发送确认");
                dlg.Commands.Add(new UICommand("发送", null, 1));
                dlg.Commands.Add(new UICommand("取消", null, 0));
                dlg.DefaultCommandIndex = 0;
                dlg.CancelCommandIndex = 1;
                var result = await dlg.ShowAsync();
                if (result == null || !(result.Id is int id) || id != 1) return;
            }
            await _vm.SendAsync();
            ScrollToBottom();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectMode)
            {
                ExitMultiSelectMode();
                return;
            }
            if (Frame.CanGoBack) Frame.GoBack();
        }

    }
}
