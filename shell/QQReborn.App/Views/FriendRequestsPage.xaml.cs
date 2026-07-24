using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class FriendRequestsPage : Page
    {
        private readonly FriendRequestsViewModel _vm;

        public FriendRequestsPage()
        {
            InitializeComponent();
            _vm = new FriendRequestsViewModel(App.ChatService);
            DataContext = _vm;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await _vm.LoadAsync();
        }

        private async void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FriendRequest request)
            {
                await _vm.AcceptAsync(request);
                // The real backend honestly reports handled:false -- LagrangeV2 has no API
                // to accept friend requests. Without this the button just silently does
                // nothing, which reads as "the app is broken" instead of "not supported".
                if (!request.Handled)
                {
                    var dialog = new Windows.UI.Popups.MessageDialog(
                        "当前后端暂不支持同意好友请求（协议库没有这个接口），请先在手机官方 QQ 上操作。", "暂不支持");
                    try { await dialog.ShowAsync(); } catch (Exception) { }
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void GroupNotifButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(GroupNotificationsPage));
        }
    }
}
