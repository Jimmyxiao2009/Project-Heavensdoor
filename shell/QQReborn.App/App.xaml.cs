using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Services;
using QQReborn.App.Views;

namespace QQReborn.App
{
    public sealed partial class App : Application
    {
        /// <summary>
        /// App-wide chat backend. Flip to false to use the in-app mock instead of a
        /// WebSocket server (QQReborn.FakeServer for the demo, or QQReborn.RealServer for
        /// a real LagrangeV2-backed QQ account -- both speak the same protocol on :8765).
        /// </summary>
        private const bool UseRemoteBackend = true;

        public static IChatService ChatService { get; } =
            UseRemoteBackend ? (IChatService)new RemoteChatService() : new MockChatService();

        /// <summary>
        /// Id of the conversation currently open on screen, or null if none. Used so the
        /// conversation list does not bump an unread badge for messages the user is reading live.
        /// Set by ConversationPage on navigation in, cleared on navigation out.
        /// </summary>
        public static string ActiveConversationId { get; set; }

        /// <summary>Guards against registering the hardware/back-button handler more than
        /// once -- OnLaunched can run again (e.g. after a Prelaunch activation).</summary>
        private bool _backHandlerRegistered;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            Suspending += OnSuspending;
            Resuming += OnResuming;
            // Keep unread badges accumulating while MainPage is not on screen
            // (inside a chat / settings / suspended UI). Server also tracks; list merges both.
            UnreadBadgeStore.EnsureHooked(ChatService);
            if (UseRemoteBackend)
            {
                ChatService.MessageReceived += OnMessageReceivedForToast;
            }
        }

        private async void OnMessageReceivedForToast(object sender, Models.ChatMessage msg)
        {
            if (msg == null || msg.Direction != Models.MessageDirection.Incoming) return;
            if (msg.ConversationId == ActiveConversationId) return;

            // Settings: 消息通知 (default on)
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values["qqr.settings.notifications"];
                if (raw is bool on && !on) return;
            }
            catch { /* default on */ }

            // Global "全部消息免打扰" + per-conversation mute. 特别关心 still notifies.
            // ConversationCache / IsMuted can lag; LocalSettings is the source of truth.
            if (Services.NotificationMuteGate.ShouldSuppressNotification(msg.ConversationId)) return;

            string preview = msg.Text ?? "";
            switch (msg.ContentType)
            {
                case Models.MessageContentType.Image: preview = "[图片]"; break;
                case Models.MessageContentType.Sticker: preview = "[表情]"; break;
                case Models.MessageContentType.Voice: preview = "[语音]"; break;
                case Models.MessageContentType.Location: preview = "[位置]"; break;
                case Models.MessageContentType.Video: preview = "[视频]"; break;
                case Models.MessageContentType.FileMsg: preview = "[文件]"; break;
            }

            string title = !string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle : msg.SenderName;
            string content = preview;
            string avatar = !string.IsNullOrEmpty(msg.ConversationAvatarPath) ? msg.ConversationAvatarPath : msg.SenderAvatarPath;
            bool isGroupPush = !string.IsNullOrEmpty(msg.ConversationId)
                && msg.ConversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase);

            try
            {
                // Use the app's local conversation cache only for title/avatar formatting.
                // Do not trust its IsMuted for toast emission.
                var convs = await ConversationCache.LoadAsync();
                foreach (var c in convs)
                {
                    if (c.Id != msg.ConversationId) continue;
                    if (c.Kind == Models.ConversationKind.Group || isGroupPush)
                    {
                        if (!string.IsNullOrEmpty(c.Title)) title = c.Title;
                        content = msg.SenderName + ": " + preview;
                        if (!string.IsNullOrEmpty(c.AvatarPath)) avatar = c.AvatarPath;
                    }
                    else if (!string.IsNullOrEmpty(c.Title))
                    {
                        title = c.Title;
                    }
                    break;
                }
            }
            catch { }

            ToastHelper.ShowMessageToast(title, msg.ConversationId, content, avatar, false);
        }

        private void OnResuming(object sender, object e)
        {
            if (UseRemoteBackend && ChatService is RemoteChatService remoteChat)
            {
                var _ = remoteChat.ForceReconnectAsync();
            }
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            System.Diagnostics.Debug.WriteLine(">>>>> QQREBORN UNHANDLED >>>>>");
            System.Diagnostics.Debug.WriteLine("MESSAGE: " + e.Message);
            System.Diagnostics.Debug.WriteLine("EXCEPTION: " + (ex != null ? ex.ToString() : "(null)"));
            System.Diagnostics.Debug.WriteLine(">>>>> END >>>>>");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame rootFrame))
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (!_backHandlerRegistered)
            {
                _backHandlerRegistered = true;
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }

                Window.Current.Activate();
            }
        }

        /// <summary>Hardware/on-screen back button: go back within the app instead of the
        /// OS default of exiting it. If the root frame can't go back further, leave e.Handled
        /// false so the platform falls through to its normal behavior (leave the app).</summary>
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Window.Current.Content is Frame rootFrame && rootFrame.CanGoBack)
            {
                e.Handled = true;
                rootFrame.GoBack();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _extendedSession;

        private async void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            if (_extendedSession != null)
            {
                _extendedSession.Dispose();
                _extendedSession = null;
            }

            _extendedSession = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession
            {
                Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified,
                Description = "Keep receiving WebSocket messages for Toast notifications"
            };

            _extendedSession.Revoked += (s, args) =>
            {
                if (_extendedSession != null)
                {
                    _extendedSession.Dispose();
                    _extendedSession = null;
                }
            };

            var result = await _extendedSession.RequestExtensionAsync();
            // If Allowed, the app continues running in the background when minimized.
            // If Denied, it suspends as usual.

            deferral.Complete();
        }
    }
}
