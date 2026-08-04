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
                // NapCat path should set handled:true; if not, surface failure instead of silence.
                if (!request.Handled)
                {
                    var dialog = new Windows.UI.Popups.MessageDialog(
                        "同意失败，请确认 NapCat 在线且请求未过期。", "提示");
                    try { await dialog.ShowAsync(); } catch (Exception) { }
                }
            }
        }

        private async void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FriendRequest request)
            {
                await _vm.RejectAsync(request);
                if (!request.Handled)
                {
                    var dialog = new Windows.UI.Popups.MessageDialog(
                        "拒绝失败，请确认 NapCat 在线且请求未过期。", "提示");
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
