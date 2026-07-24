using System;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
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
            SignTokenBox.Password = _vm.SignToken;
        }

        private void SignTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            _vm.SignToken = SignTokenBox.Password;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            await _vm.ClearCacheAsync();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            Frame?.Navigate(typeof(AccountLoginPage));
        }

        private async void PickDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                StorageApplicationPermissions.FutureAccessList.AddOrReplace("DownloadFolder", folder);
                _vm.DownloadFolderPath = folder.Path;
            }
        }
    }
}
