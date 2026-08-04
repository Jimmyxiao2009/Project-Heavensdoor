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
    public sealed partial class MainPage
    {
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
                AppNav.ToRoot(typeof(ConversationPage), conv);
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
                await ConversationNotificationSettings.TrySetMutedAsync(App.ChatService, conv, next);
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
                    if (AppServices.Gateway is IGatewayService remote)
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
                info.Click += (s, e) => AppNav.ToRoot(typeof(GroupInfoPage), conv);
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
            add.Click += (s, args) => AppNav.ToRoot(typeof(AddFriendPage));
            menu.Items.Add(add);
            if (sender is FrameworkElement fe)
                menu.ShowAt(fe);
        }

        private void NewFriends_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AppNav.ToRoot(typeof(FriendRequestsPage));
        }

        private void GroupNotifications_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AppNav.ToRoot(typeof(GroupNotificationsPage));
        }
    }
}
