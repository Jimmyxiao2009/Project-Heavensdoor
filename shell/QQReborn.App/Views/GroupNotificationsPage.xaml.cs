using System;
using System.Collections.ObjectModel;
using QQReborn.App.Services;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace QQReborn.App.Views
{
    public class GroupNotificationItem
    {
        public long GroupUin { get; set; }
        public ulong Sequence { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string AvatarPath { get; set; }
        public bool IsFiltered { get; set; }
    }

    public sealed partial class GroupNotificationsPage : Page
    {
        public ObservableCollection<GroupNotificationItem> Items { get; } = new ObservableCollection<GroupNotificationItem>();

        public GroupNotificationsPage()
        {
            this.InitializeComponent();
            NotificationList.ItemsSource = Items;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadNotificationsAsync();
        }

        private async System.Threading.Tasks.Task LoadNotificationsAsync()
        {
            Items.Clear();
            if (AppServices.Gateway is IGatewayService remote)
            {
                try
                {
                    var arr = await remote.GetGroupNotificationsAsync();
                    foreach (var val in arr)
                    {
                        var obj = val.GetObject();
                        var gUin = (long)obj.GetNamedNumber("groupUin", 0);
                        var seq = (ulong)obj.GetNamedNumber("sequence", 0);
                        var type = obj.GetNamedString("type", "join");
                        var msg = obj.GetNamedString("message", "");
                        var nickname = obj.GetNamedString("initiatorNickname", "成员");
                        var avatar = obj.GetNamedString("avatarPath", "");

                        var filtered = obj.GetNamedBoolean("isFiltered", false);
                        Items.Add(new GroupNotificationItem
                        {
                            GroupUin = gUin,
                            Sequence = seq,
                            Type = type,
                            Title = type == "invite" ? nickname + " 邀请入群" : nickname + " 申请加群",
                            Message = string.IsNullOrEmpty(msg) ? "群号: " + gUin : msg,
                            AvatarPath = avatar,
                            IsFiltered = filtered
                        });
                    }
                }
                catch { }
            }

            EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void Accept_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GroupNotificationItem item && AppServices.Gateway is IGatewayService remote)
            {
                try
                {
                    var ok = await remote.HandleGroupNotificationAsync(item.GroupUin, item.Sequence, item.Type, "allow", "", item.IsFiltered);
                    if (ok) Items.Remove(item);
                }
                catch { }
                EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void Reject_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GroupNotificationItem item && AppServices.Gateway is IGatewayService remote)
            {
                try
                {
                    var ok = await remote.HandleGroupNotificationAsync(item.GroupUin, item.Sequence, item.Type, "deny", "", item.IsFiltered);
                    if (ok) Items.Remove(item);
                }
                catch { }
                EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
