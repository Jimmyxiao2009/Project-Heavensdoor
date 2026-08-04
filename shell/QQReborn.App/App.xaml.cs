using System;
using System.Reflection;
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
        /// App-wide chat backend. Toggle via <see cref="AppServices.UseRemoteBackend"/>.
        /// Prefer <see cref="AppServices"/> for profile/moments/search accessors.
        /// </summary>
        public static IChatService ChatService => AppServices.Chat;

        /// <summary>
        /// Id of the conversation currently open on screen, or null if none. Used so the
        /// conversation list does not bump an unread badge for messages the user is reading live.
        /// Set by ConversationPage on navigation in, cleared on navigation out.
        /// </summary>
        public static string ActiveConversationId { get; set; }

        // A minimized UWP process may be terminated and launched again instead of being
        // resumed. Keep just enough metadata to reopen the chat that was on screen.
        private const string SuspendedConversationIdKey = "qqr.lifecycle.conversation.id";
        private const string SuspendedConversationKindKey = "qqr.lifecycle.conversation.kind";
        private const string SuspendedConversationTitleKey = "qqr.lifecycle.conversation.title";
        private const string SuspendedConversationAvatarKey = "qqr.lifecycle.conversation.avatar";

        public static void RememberConversation(Models.ChatConversation conversation)
        {
            if (conversation == null || string.IsNullOrEmpty(conversation.Id)) return;
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                values[SuspendedConversationIdKey] = conversation.Id;
                values[SuspendedConversationKindKey] = (int)conversation.Kind;
                values[SuspendedConversationTitleKey] = conversation.Title ?? conversation.Id;
                values[SuspendedConversationAvatarKey] = conversation.AvatarPath ?? string.Empty;
            }
            catch { }
        }

        public static Models.ChatConversation RestoreRememberedConversation()
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                var id = values[SuspendedConversationIdKey] as string;
                if (string.IsNullOrEmpty(id)) return null;

                var kind = Models.ConversationKind.Friend;
                var rawKind = values[SuspendedConversationKindKey];
                if (rawKind is int i) kind = (Models.ConversationKind)i;
                else if (rawKind is long l) kind = (Models.ConversationKind)l;

                return new Models.ChatConversation
                {
                    Id = id,
                    Kind = kind,
                    Title = values[SuspendedConversationTitleKey] as string ?? id,
                    AvatarPath = values[SuspendedConversationAvatarKey] as string,
                };
            }
            catch { return null; }
        }

        public static void ClearRememberedConversation()
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                values.Remove(SuspendedConversationIdKey);
                values.Remove(SuspendedConversationKindKey);
                values.Remove(SuspendedConversationTitleKey);
                values.Remove(SuspendedConversationAvatarKey);
            }
            catch { }
        }

        /// <summary>Guards against registering the hardware/back-button handler more than
        /// once -- OnLaunched can run again (e.g. after a Prelaunch activation).</summary>
        private bool _backHandlerRegistered;
        private int _resumeInFlight;

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            Suspending += OnSuspending;
            Resuming += OnResuming;
            // Keep unread badges accumulating while MainPage is not on screen
            // (inside a chat / settings / suspended UI). Server also tracks; list merges both.
            UnreadBadgeStore.EnsureHooked(ChatService);
            if (AppServices.UseRemoteBackend)
            {
                ChatService.MessageReceived += OnMessageReceivedForToast;
                // Connect as soon as the process starts so unread/push work even before
                // the user lands on MainPage and LoadAsync runs.
                AppServices.Gateway?.StartAutoConnect();
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

            string preview = MessagePresentation.GetSummary(msg);

            bool isGroupPush = !string.IsNullOrEmpty(msg.ConversationId)
                && (msg.ConversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase)
                    || msg.ConversationId.StartsWith("G", StringComparison.Ordinal));

            // Defaults from the wire payload (server now fills conversation* for groups).
            string title = !string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle : msg.SenderName;
            string content = preview;
            string avatar = !string.IsNullOrEmpty(msg.ConversationAvatarPath)
                ? msg.ConversationAvatarPath
                : msg.SenderAvatarPath;

            // Always format group toasts as: group name + "成员: 内容" + group avatar.
            if (isGroupPush)
            {
                content = string.IsNullOrEmpty(msg.SenderName) ? preview : (msg.SenderName + ": " + preview);
                // Synthetic group avatar if server/cache still empty (qlogo CDN).
                if (string.IsNullOrEmpty(avatar) && msg.ConversationId.Length > 1)
                {
                    var peer = msg.ConversationId.Substring(1);
                    long g;
                    if (long.TryParse(peer, out g) && g > 0)
                        avatar = "https://p.qlogo.cn/gh/" + g.ToString() + "/" + g.ToString() + "/100";
                }
                if (string.IsNullOrEmpty(title) || title == msg.SenderName)
                {
                    // Prefer peer uin as last-resort title over a misleading member name.
                    if (msg.ConversationId.Length > 1) title = msg.ConversationId.Substring(1);
                }
            }

            // Prefer wire metadata. Only hit disk cache when title/avatar are still empty —
            // LoadAsync on every toast was measurable jank under message bursts on phone.
            bool needCache = string.IsNullOrEmpty(title)
                || (isGroupPush && string.IsNullOrEmpty(msg.ConversationAvatarPath) && string.IsNullOrEmpty(avatar))
                || (!isGroupPush && string.IsNullOrEmpty(avatar));
            if (needCache)
            {
                try
                {
                    var convs = await ConversationCache.LoadAsync();
                    foreach (var c in convs)
                    {
                        if (c == null || c.Id != msg.ConversationId) continue;
                        var group = c.Kind == Models.ConversationKind.Group || isGroupPush;
                        if (group)
                        {
                            if (!string.IsNullOrEmpty(c.Title)) title = c.Title;
                            content = string.IsNullOrEmpty(msg.SenderName) ? preview : (msg.SenderName + ": " + preview);
                            if (!string.IsNullOrEmpty(c.AvatarPath)) avatar = c.AvatarPath;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(c.Title)) title = c.Title;
                            if (!string.IsNullOrEmpty(c.AvatarPath)) avatar = c.AvatarPath;
                        }
                        break;
                    }
                }
                catch { }
            }

            // Friend fallback: if still no avatar, use sender avatar / qlogo.
            if (!isGroupPush && string.IsNullOrEmpty(avatar))
            {
                avatar = msg.SenderAvatarPath;
                if (string.IsNullOrEmpty(avatar) && msg.SenderUin > 0)
                    avatar = "https://q1.qlogo.cn/g?b=qq&nk=" + msg.SenderUin.ToString() + "&s=100";
            }

            ToastHelper.ShowMessageToast(title, msg.ConversationId, content, avatar, false);

            // Optional phone vibration (实用功能 · 来消息震动). Phone-only API; reflection
            // keeps the desktop UWP build from requiring Windows.Phone contracts.
            if (UtilitySettings.VibrateOnMessage)
            {
                try
                {
                    var t = Type.GetType("Windows.Phone.Devices.Notification.VibrationDevice, Windows, ContentType=WindowsRuntime");
                    if (t != null)
                    {
                        var getDefault = t.GetRuntimeMethod("GetDefault", Type.EmptyTypes);
                        var device = getDefault?.Invoke(null, null);
                        var vibrate = t.GetRuntimeMethod("Vibrate", new[] { typeof(TimeSpan) });
                        vibrate?.Invoke(device, new object[] { TimeSpan.FromMilliseconds(120) });
                    }
                }
                catch { /* desktop / no vibration device */ }
            }
        }

        private async void OnResuming(object sender, object e)
        {
            var remoteChat = AppServices.Gateway;
            if (remoteChat == null) return;
            if (System.Threading.Interlocked.Exchange(ref _resumeInFlight, 1) != 0) return;
            try
            {
                await remoteChat.ForceReconnectAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Resume reconnect failed: " + ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _resumeInFlight, 0);
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
            // Plain Frame as Window.Content — never wrap/scale the root. Root ScaleTransform
            // freezes MainPage (Pivot measure loop) and left white bars on phone.
            if (!(Window.Current.Content is Frame rootFrame))
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            // Density tokens only (bindable sizes); no layout transform.
            UiScaleService.ApplyFromSettings();

            if (!_backHandlerRegistered)
            {
                _backHandlerRegistered = true;
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // A real launch starts at Home. ConversationPage is retained by the
                    // existing Frame while the app is suspended/minimized, so resume still
                    // stays in the chat without making the last chat the permanent home page.
                    rootFrame.Navigate(typeof(ShellPage), e.Arguments);
                }

                Window.Current.Activate();
            }
        }

        /// <summary>Hardware/on-screen back button: go back within the app instead of the
        /// OS default of exiting it. If the root frame can't go back further, leave e.Handled
        /// false so the platform falls through to its normal behavior (leave the app).</summary>
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (AppNav.GoBack())
                e.Handled = true;
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _extendedSession;

        private async void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (_extendedSession != null)
                {
                    _extendedSession.Dispose();
                    _extendedSession = null;
                }

                var session = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession
                {
                    Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified,
                    Description = "Keep receiving WebSocket messages for Toast notifications"
                };
                _extendedSession = session;

                session.Revoked += (s, args) =>
                {
                    if (object.ReferenceEquals(_extendedSession, session))
                    {
                        _extendedSession = null;
                        session.Dispose();
                    }
                };

                // If Allowed, the app continues running in the background when minimized.
                // If Denied or unavailable, it suspends normally and the saved chat metadata
                // lets a later process recreation reopen the same page.
                await session.RequestExtensionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Extended execution unavailable: " + ex);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
