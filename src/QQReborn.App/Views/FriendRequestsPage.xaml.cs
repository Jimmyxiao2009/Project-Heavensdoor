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
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
