using System;
using System.Linq;
using System.Collections.Generic;
using Windows.Storage;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class MainPage : Page
    {
        private readonly MainViewModel _vm;

        // Persisted "消息通知" preference (LocalSettings key shared with MockProfileService).
        private const string NotificationsSettingKey = "qqr.settings.notifications";

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            _vm = new MainViewModel(App.ChatService);
            DataContext = _vm;
        }

        // ---- 应用内新消息横幅 ----

        private async void OnGlobalMessageReceived(object sender, ChatMessage msg)
        {
            if (msg == null) return;
            // 自己在其他设备发出的消息（真实后端会以 Outgoing 推送回声）不该弹通知。
            if (msg.Direction == MessageDirection.Outgoing) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // 关闭"消息通知"开关后不弹横幅。
                if (!NotificationsEnabled()) return;

                // 全部消息免打扰 / 单会话免打扰（特别关心可穿透）。
                if (NotificationMuteGate.ShouldSuppressNotification(msg.ConversationId)) return;

                // 仅当当前停留在 MainPage 时才弹横幅。
                if ((Window.Current.Content as Frame)?.CurrentSourcePageType != typeof(MainPage)) return;

                // 按会话 Id 找到对应会话；找不到则不弹（列表尚未合成时由 toast 侧负责）。
                var conv = _vm.Conversations.FirstOrDefault(c => c.Id == msg.ConversationId);
                // Mute already checked via NotificationMuteGate; ignore lagging conv.IsMuted.

                var preview = msg.Text ?? "";
                switch (msg.ContentType)
                {
                    case MessageContentType.Image: preview = "[图片]"; break;
                    case MessageContentType.Sticker: preview = "[表情]"; break;
                    case MessageContentType.Voice: preview = "[语音]"; break;
                    case MessageContentType.Location: preview = "[位置]"; break;
                    case MessageContentType.Video: preview = "[视频]"; break;
                    case MessageContentType.FileMsg: preview = "[文件]"; break;
                }

                var isGroup = (conv != null && conv.IsGroup)
                    || (!string.IsNullOrEmpty(msg.ConversationId)
                        && msg.ConversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase));

                string title;
                string avatar;
                string body;
                if (isGroup)
                {
                    title = !string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle
                        : (conv != null && !string.IsNullOrEmpty(conv.Title) ? conv.Title : msg.SenderName);
                    avatar = !string.IsNullOrEmpty(msg.ConversationAvatarPath) ? msg.ConversationAvatarPath
                        : (conv != null ? conv.AvatarPath : null);
                    if (string.IsNullOrEmpty(avatar) && !string.IsNullOrEmpty(msg.ConversationId) && msg.ConversationId.Length > 1)
                    {
                        long g;
                        if (long.TryParse(msg.ConversationId.Substring(1), out g) && g > 0)
                            avatar = "https://p.qlogo.cn/gh/" + g + "/" + g + "/100";
                    }
                    body = string.IsNullOrEmpty(msg.SenderName) ? preview : (msg.SenderName + ": " + preview);
                }
                else
                {
                    title = conv != null && !string.IsNullOrEmpty(conv.Title) ? conv.Title
                        : (!string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle : msg.SenderName);
                    avatar = conv != null && !string.IsNullOrEmpty(conv.AvatarPath) ? conv.AvatarPath
                        : (!string.IsNullOrEmpty(msg.ConversationAvatarPath) ? msg.ConversationAvatarPath : msg.SenderAvatarPath);
                    body = preview;
                }

                if (conv != null)
                {
                    Banner.Show(avatar, title, body,
                        () => Frame.Navigate(typeof(ConversationPage), conv));
                }
                else
                {
                    // List row not ready yet — still show banner with wire meta; tap is no-op.
                    Banner.Show(avatar, title, body, null);
                }

            });
        }

        /// <summary>Read the persisted 消息通知 toggle; defaults to true when unset.</summary>
        private static bool NotificationsEnabled()
        {
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values[NotificationsSettingKey];
                return !(raw is bool b) || b;
            }
            catch
            {
                return true;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // This method must contain no await. A cached MainPage should return to the
            // frame immediately; all cache/network work is started after navigation.
            App.ChatService.MessageReceived += OnGlobalMessageReceived;
            _vm.Attach();
            _ = LoadHomeInBackgroundAsync();
        }

        private async System.Threading.Tasks.Task LoadHomeInBackgroundAsync()
        {
            try
            {
                if (!_vm.IsLoaded)
                    await _vm.LoadAsync();
                await _vm.SoftRefreshAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Home background load failed: " + ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // NavigationCacheMode keeps this page instance alive. Always reset the
            // transient drawer before leaving, otherwise the dimmer/panel can remain
            // above the cached page when returning and UWP may dispatch stale pointer
            // events into a detached visual tree.
            ResetQuickPanelState();
            base.OnNavigatedFrom(e);
            App.ChatService.MessageReceived -= OnGlobalMessageReceived;
            _vm.Detach();
        }

        private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatConversation conv)
            {
                conv.Unread = 0;
                UnreadBadgeStore.Clear(conv.Id);
                Frame.Navigate(typeof(ConversationPage), conv);
            }
        }

        private void SearchBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage), null);
        }

        private void SearchAppBar_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage), null);
        }

        private async void RefreshAppBar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.SoftRefreshAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Refresh failed: " + ex);
            }
        }

        private void QuickPanelAppBar_Click(object sender, RoutedEventArgs e)
        {
            ToggleQuickPanel();
        }

        private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Contact contact)
            {
                Frame.Navigate(typeof(ContactDetailPage), contact);
            }
        }


        private double _panelY = -280;

        private void Title_Click(object sender, RoutedEventArgs e)
        {
            ToggleQuickPanel();
        }

        private void ToggleQuickPanel()
        {
            bool open = QuickPanel.Visibility != Visibility.Visible || _panelY < 0;
            AnimateQuickPanel(open);
        }

        private void QuickDimmer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            AnimateQuickPanel(false);
        }

        private void QuickDimmer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
            AnimateQuickPanel(false);
        }

        private void QuickPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // The transformed Border still owns its full 300px hit-test rectangle
            // while half-open. Let taps on its transparent lower area close it.
            var point = e.GetPosition(QuickPanel);
            if (point.Y > 180)
            {
                e.Handled = true;
                AnimateQuickPanel(false);
            }
        }

        private void QuickPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(QuickPanel).Position;
            if (point.Y > 180)
            {
                e.Handled = true;
                AnimateQuickPanel(false);
            }
        }

        // Quick panel height must match MainPage.xaml Border Height / initial TranslateY.
        private const double QuickPanelHeight = 280;

        private void ResetQuickPanelState()
        {
            var panelTransform = QuickPanel?.RenderTransform as CompositeTransform;
            var pageTransform = PageContent?.RenderTransform as CompositeTransform;
            if (panelTransform != null) panelTransform.TranslateY = -QuickPanelHeight;
            if (pageTransform != null)
            {
                pageTransform.TranslateY = 0;
                pageTransform.ScaleX = 1;
                pageTransform.ScaleY = 1;
            }
            if (QuickPanel != null) QuickPanel.Visibility = Visibility.Collapsed;
            if (QuickDimmer != null) QuickDimmer.Visibility = Visibility.Collapsed;
            _panelY = -QuickPanelHeight;
        }

        private void AnimateQuickPanel(bool open)
        {
            var panelTransform = QuickPanel?.RenderTransform as CompositeTransform;
            var pageTransform = PageContent?.RenderTransform as CompositeTransform;
            if (panelTransform == null || pageTransform == null) return;

            QuickPanel.Visibility = Visibility.Visible;
            QuickDimmer.Visibility = Visibility.Visible;

            var panelAnimation = new DoubleAnimation
            {
                To = open ? 0 : -QuickPanelHeight,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true
            };
            var pageYAnimation = new DoubleAnimation
            {
                To = open ? 48 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true
            };
            var scaleAnimation = new DoubleAnimation
            {
                To = open ? 0.965 : 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(panelAnimation, panelTransform);
            Storyboard.SetTargetProperty(panelAnimation, "TranslateY");
            Storyboard.SetTarget(pageYAnimation, pageTransform);
            Storyboard.SetTargetProperty(pageYAnimation, "TranslateY");
            Storyboard.SetTarget(scaleAnimation, pageTransform);
            Storyboard.SetTargetProperty(scaleAnimation, "ScaleX");
            var scaleYAnimation = new DoubleAnimation
            {
                To = open ? 0.965 : 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(scaleYAnimation, pageTransform);
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");

            var board = new Storyboard();
            board.Children.Add(panelAnimation);
            board.Children.Add(pageYAnimation);
            board.Children.Add(scaleAnimation);
            board.Children.Add(scaleYAnimation);
            board.Completed += (s, e) =>
            {
                _panelY = open ? 0 : -QuickPanelHeight;
                if (!open)
                {
                    QuickPanel.Visibility = Visibility.Collapsed;
                    QuickDimmer.Visibility = Visibility.Collapsed;
                }
            };
            board.Begin();
        }

        private void Header_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            var dy = e.Cumulative.Translation.Y;
            if (dy <= 0 && _panelY <= 0) return;
            _panelY = Math.Max(-QuickPanelHeight, Math.Min(0, -QuickPanelHeight + dy));
            QuickPanel.Visibility = Visibility.Visible;
            QuickDimmer.Visibility = Visibility.Visible;
            if (QuickPanel.RenderTransform is CompositeTransform transform)
                transform.TranslateY = _panelY;
            if (PageContent.RenderTransform is CompositeTransform pageTransform)
            {
                pageTransform.TranslateY = Math.Max(0, 48 + _panelY * 0.17);
                pageTransform.ScaleX = pageTransform.ScaleY = 0.965;
            }
        }

        private void Header_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            var open = _panelY > -(QuickPanelHeight * 0.5);
            AnimateQuickPanel(open);
        }

        private async void MuteAll_Click(object sender, RoutedEventArgs e)
        {
            await ApplyMuteAllAsync(true);
        }

        private async void UnmuteAll_Click(object sender, RoutedEventArgs e)
        {
            await ApplyMuteAllAsync(false);
        }

        private async System.Threading.Tasks.Task ApplyMuteAllAsync(bool muted)
        {
            // Global gate first: toast/banner must suppress even conversations that are
            // not in the list yet (new friend/group after this toggle).
            NotificationMuteGate.SetMuteAll(muted);

            // One-tap mute must not rely only on the currently rendered rows. The home
            // list can still be empty/partial right after login; pull the full set first.
            try
            {
                if (!_vm.IsLoaded) await _vm.LoadAsync();
                await _vm.SoftRefreshAsync();
            }
            catch { }

            var targets = _vm.Conversations.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && !IsSpecial(c)).ToList();
            if (App.ChatService is RemoteChatService remote)
            {
                try
                {
                    var fresh = await remote.GetConversationsAsync();
                    if (fresh != null)
                    {
                        var seen = new HashSet<string>(targets.Select(t => t.Id));
                        foreach (var c in fresh)
                        {
                            if (c == null || string.IsNullOrEmpty(c.Id) || IsSpecial(c)) continue;
                            if (seen.Contains(c.Id)) continue;
                            targets.Add(c);
                            seen.Add(c.Id);
                        }
                    }
                }
                catch { }
            }

            foreach (var c in targets)
            {
                c.IsMuted = muted;
                await SetMuteSafe(c, muted);
            }

            AnimateQuickPanel(false);
        }

        private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
        {
            var pending = new List<System.Threading.Tasks.Task>();
            foreach (var c in _vm.Conversations)
            {
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                c.Unread = 0;
                UnreadBadgeStore.Clear(c.Id);
                if (App.ChatService is RemoteChatService remote)
                    pending.Add(MarkReadSafeAsync(remote, c.Id));
            }

            if (pending.Count > 0)
                await System.Threading.Tasks.Task.WhenAll(pending);

            AnimateQuickPanel(false);
        }

        private static async System.Threading.Tasks.Task MarkReadSafeAsync(RemoteChatService remote, string conversationId)
        {
            try { await remote.MarkConversationReadAsync(conversationId, System.DateTimeOffset.UtcNow.ToString("o")); }
            catch { }
        }

        private static bool IsSpecial(ChatConversation c)
            => c != null && NotificationMuteGate.IsSpecial(c.Id);

        private static async System.Threading.Tasks.Task SetMuteSafe(ChatConversation c, bool value)
        {
            if (c == null || string.IsNullOrEmpty(c.Id)) return;
            // Write the local notification gate before the network round-trip. Toasts can
            // arrive while a batch is still updating the server one conversation at a time.
            NotificationMuteGate.SetConversationMuted(c.Id, value);
            try
            {
                await App.ChatService.SetConversationFlagsAsync(c.Id, null, value);
            }
            catch { }
        }

        private void Files_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ProfilePlaceholderPage), "files");
        }

        // ---- 会话列表 长按 / 右键 菜单 ----

        private void Conversation_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            ShowConversationMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Conversation_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Touch/pen right-taps are already covered by Conversation_Holding (long-press);
            // without this guard the same touch gesture fires both handlers and pops two
            // stacked context menus. Only a real mouse right-click should land here.
            if (e.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Mouse) return;
            ShowConversationMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowConversationMenu(FrameworkElement anchor)
        {
            if (!(anchor?.DataContext is ChatConversation conv)) return;

            var menu = new MenuFlyout();

            var open = new MenuFlyoutItem { Text = "打开会话" };
            open.Click += (s, e) =>
            {
                conv.Unread = 0;
                UnreadBadgeStore.Clear(conv.Id);
                Frame.Navigate(typeof(ConversationPage), conv);
            };
            menu.Items.Add(open);

            var pin = new MenuFlyoutItem { Text = conv.IsPinned ? "取消置顶" : "置顶聊天" };
            pin.Click += async (s, e) =>
            {
                var next = !conv.IsPinned;
                conv.IsPinned = next;
                _vm.ResortConversations();
                try { await App.ChatService.SetConversationFlagsAsync(conv.Id, next, null); }
                catch
                {
                    conv.IsPinned = !next;
                    _vm.ResortConversations();
                }
            };
            menu.Items.Add(pin);

            // The row's IsMuted may lag behind the persistent gate after reconnect; use
            // the same LocalSettings keys that suppress app-level notifications.
            if (NotificationMuteGate.IsConversationMuted(conv.Id) || NotificationMuteGate.IsMuteAll())
                conv.IsMuted = true;
            var mute = new MenuFlyoutItem { Text = conv.IsMuted ? "取消免打扰" : "消息免打扰" };
            mute.Click += async (s, e) =>
            {
                var next = !conv.IsMuted;
                conv.IsMuted = next;
                NotificationMuteGate.SetConversationMuted(conv.Id, next);
                // Turning a single chat back on should not leave global mute-all stuck.
                if (!next && NotificationMuteGate.IsMuteAll())
                    NotificationMuteGate.SetMuteAll(false);
                if (next) UnreadBadgeStore.Clear(conv.Id);
                try { await App.ChatService.SetConversationFlagsAsync(conv.Id, null, next); }
                catch { conv.IsMuted = !next; }
            };
            menu.Items.Add(mute);

            var special = new MenuFlyoutItem { Text = IsSpecial(conv) ? "取消特别关注" : "设为特别关注" };
            special.Click += (s, e) =>
            {
                var next = !IsSpecial(conv);
                NotificationMuteGate.SetSpecial(conv.Id, next);
                special.Text = next ? "取消特别关注" : "设为特别关注";
            };
            menu.Items.Add(special);

            if (conv.Unread > 0)
            {
                var read = new MenuFlyoutItem { Text = "标为已读" };
                read.Click += (s, e) =>
                {
                    conv.Unread = 0;
                    UnreadBadgeStore.Clear(conv.Id);
                    if (App.ChatService is RemoteChatService remote)
                    {
                        var _ = remote.MarkConversationReadAsync(conv.Id, System.DateTimeOffset.UtcNow.ToString("o"));
                    }
                };
                menu.Items.Add(read);
            }
            else
            {
                var unread = new MenuFlyoutItem { Text = "标为未读" };
                unread.Click += (s, e) =>
                {
                    if (conv.Unread <= 0) conv.Unread = 1;
                    UnreadBadgeStore.SetAtLeast(conv.Id, conv.Unread);
                };
                menu.Items.Add(unread);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            if (conv.Kind == ConversationKind.Group)
            {
                var info = new MenuFlyoutItem { Text = "群资料" };
                info.Click += (s, e) => Frame.Navigate(typeof(GroupInfoPage), conv);
                menu.Items.Add(info);
            }

            var del = new MenuFlyoutItem { Text = "从列表移除" };
            del.Click += (s, e) => _vm.Conversations.Remove(conv);
            menu.Items.Add(del);

            menu.ShowAt(anchor);
        }

        // ---- 顶部 "+" 菜单 ----

        private void AddMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            // 发起群聊 / 扫一扫：无协议实现，已移除入口。
            var add = new MenuFlyoutItem { Text = "加好友/加群" };
            add.Click += (s, args) => Frame.Navigate(typeof(AddFriendPage));
            menu.Items.Add(add);
            menu.ShowAt(sender as FrameworkElement);
        }

        // ---- 新朋友 入口 ----

        private void NewFriends_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Frame.Navigate(typeof(FriendRequestsPage));
        }

        private void GroupNotifications_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Frame.Navigate(typeof(GroupNotificationsPage));
        }
    }
}
