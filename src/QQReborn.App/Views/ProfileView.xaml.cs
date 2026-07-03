using System;
using System.ComponentModel;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    /// <summary>
    /// The "我" (profile) area, embeddable into MainPage's 我 pivot item.
    /// Owns its own ProfileViewModel and a private profile service so it is
    /// self-contained; reads self identity from the shared App.ChatService.
    /// </summary>
    public sealed partial class ProfileView : UserControl
    {
        private readonly ProfileViewModel _vm;

        public ProfileView()
        {
            InitializeComponent();
            _vm = new ProfileViewModel(App.ChatService, new MockProfileService());
            DataContext = _vm;
            Loaded += ProfileView_Loaded;
            Unloaded += ProfileView_Unloaded;
        }

        private async void ProfileView_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.LoadAsync();

            // The status text binds to Self.StatusText, but the colored dot is a code-built
            // brush, so paint it now and refresh it whenever the status (color) changes.
            if (_vm.Self != null)
            {
                // Detach first so repeated Loaded/Unloaded cycles don't stack duplicate
                // subscriptions on the same long-lived SelfProfile (handler leak).
                _vm.Self.PropertyChanged -= Self_PropertyChanged;
                _vm.Self.PropertyChanged += Self_PropertyChanged;
                UpdateStatusDot();
            }
        }

        private void ProfileView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_vm.Self != null) _vm.Self.PropertyChanged -= Self_PropertyChanged;
        }

        private void Self_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelfProfile.StatusColorHex) ||
                e.PropertyName == nameof(SelfProfile.Status))
            {
                UpdateStatusDot();
            }
        }

        /// <summary>Repaints the status dot from the current SelfProfile.StatusColorHex.</summary>
        private void UpdateStatusDot()
        {
            if (_vm.Self == null) return;
            StatusDot.Fill = new SolidColorBrush(ParseColor(_vm.Self.StatusColorHex));
        }

        /// <summary>Parses a "#AARRGGBB" hex string into a Windows.UI.Color.</summary>
        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Colors.Gray;
            if (hex[0] == '#') hex = hex.Substring(1);
            // Expect AARRGGBB; fall back to a neutral grey if malformed.
            if (hex.Length != 8) return Colors.Gray;
            try
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            catch (FormatException)
            {
                return Colors.Gray;
            }
        }

        private void StatusRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_vm.Self == null) return;

            var menu = new MenuFlyout();
            menu.Items.Add(BuildStatusItem("在线", OnlineStatus.Online));
            menu.Items.Add(BuildStatusItem("离开", OnlineStatus.Away));
            menu.Items.Add(BuildStatusItem("忙碌", OnlineStatus.Busy));
            menu.Items.Add(BuildStatusItem("请勿打扰", OnlineStatus.DoNotDisturb));
            menu.Items.Add(BuildStatusItem("隐身", OnlineStatus.Invisible));
            menu.ShowAt(StatusRow);
            e.Handled = true;
        }

        private MenuFlyoutItem BuildStatusItem(string text, OnlineStatus status)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, args) => _vm.SetStatus(status);
            return item;
        }

        private void MenuRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            switch (tag)
            {
                case "settings":
                    Navigate(typeof(SettingsPage));
                    break;
                case "favorites":
                case "album":
                case "files":
                case "dressup":
                    Navigate(typeof(ProfilePlaceholderPage), tag);
                    break;
            }
        }

        private void Navigate(System.Type pageType, object parameter = null)
        {
            // A UserControl has no Frame of its own; use the app's root frame.
            if (Window.Current.Content is Frame rootFrame)
            {
                rootFrame.Navigate(pageType, parameter);
            }
        }
    }
}
