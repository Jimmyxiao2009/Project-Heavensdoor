using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace QQReborn.App.Views
{
    public sealed partial class VideoPlayerPage : Page
    {
        public VideoPlayerPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string path && !string.IsNullOrEmpty(path))
            {
                Player.Source = new Uri(path);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Player.Stop();
            Player.Source = null;
            base.OnNavigatedFrom(e);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
