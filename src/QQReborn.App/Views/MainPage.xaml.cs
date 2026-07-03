using System;
using System.Linq;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
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
            _vm = new MainViewModel(App.ChatService);
            DataContext = _vm;

            App.ChatService.MessageReceived += OnGlobalMessageReceived;
        }

        // ---- 应用内新消息横幅 ----

        private async void OnGlobalMessageReceived(object sender, ChatMessage msg)
        {
            if (msg == null) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // 关闭"消息通知"开关后不弹横幅。
                if (!NotificationsEnabled()) return;

                // 仅当当前停留在 MainPage 时才弹横幅。
                if ((Window.Current.Content as Frame)?.CurrentSourcePageType != typeof(MainPage)) return;

                // 按会话 Id 找到对应会话；找不到或已设免打扰则不打扰。
                var conv = _vm.Conversations.FirstOrDefault(c => c.Id == msg.ConversationId);
                if (conv == null || conv.IsMuted) return;

                Banner.Show(conv.AvatarPath, conv.Title, msg.Text,
                    () => Frame.Navigate(typeof(ConversationPage), conv));
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

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await _vm.LoadAsync();
        }

        private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatConversation conv)
            {
                conv.Unread = 0;
                Frame.Navigate(typeof(ConversationPage), conv);
            }
        }

        private void SearchBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage), null);
        }

        private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Contact contact)
            {
                Frame.Navigate(typeof(ContactDetailPage), contact);
            }
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
            ShowConversationMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowConversationMenu(FrameworkElement anchor)
        {
            if (!(anchor?.DataContext is ChatConversation conv)) return;

            var menu = new MenuFlyout();

            var pin = new MenuFlyoutItem { Text = conv.IsPinned ? "取消置顶" : "置顶" };
            pin.Click += (s, e) =>
            {
                conv.IsPinned = !conv.IsPinned;
                _vm.ResortConversations();
            };
            menu.Items.Add(pin);

            var mute = new MenuFlyoutItem { Text = conv.IsMuted ? "取消免打扰" : "消息免打扰" };
            mute.Click += (s, e) => conv.IsMuted = !conv.IsMuted;
            menu.Items.Add(mute);

            var read = new MenuFlyoutItem { Text = "标为已读" };
            read.Click += (s, e) => conv.Unread = 0;
            menu.Items.Add(read);

            var del = new MenuFlyoutItem { Text = "删除该聊天" };
            del.Click += (s, e) => _vm.Conversations.Remove(conv);
            menu.Items.Add(del);

            menu.ShowAt(anchor);
        }

        // ---- 顶部 "+" 菜单 ----

        private void AddMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var group = new MenuFlyoutItem { Text = "发起群聊" };
            group.Click += async (s, args) => await ShowToastAsync("发起群聊");
            menu.Items.Add(group);

            var add = new MenuFlyoutItem { Text = "加好友/加群" };
            add.Click += (s, args) => Frame.Navigate(typeof(AddFriendPage));
            menu.Items.Add(add);

            var scan = new MenuFlyoutItem { Text = "扫一扫" };
            scan.Click += async (s, args) => await ShowToastAsync("扫一扫");
            menu.Items.Add(scan);

            menu.ShowAt(sender as FrameworkElement);
        }

        private static async System.Threading.Tasks.Task ShowToastAsync(string feature)
        {
            var dialog = new ContentDialog
            {
                Title = feature,
                Content = "该功能暂未开放",
                PrimaryButtonText = "知道了"
            };
            await dialog.ShowAsync();
        }

        // ---- 新朋友 入口 ----

        private void NewFriends_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Frame.Navigate(typeof(FriendRequestsPage));
        }
    }
}
