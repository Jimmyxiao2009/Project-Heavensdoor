using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    /// <summary>App settings, reached from ProfileView's 设置 row.</summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly SettingsViewModel _vm;

        public SettingsPage()
        {
            InitializeComponent();
            _vm = new SettingsViewModel(new MockProfileService());
            DataContext = _vm;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await _vm.LoadAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            await _vm.ClearCacheAsync();
        }
    }
}
