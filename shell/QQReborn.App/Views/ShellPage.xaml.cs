using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Modern app chrome: hamburger (SplitView) + bottom CommandBar primary destinations.
    /// Detail flows (chat / settings) leave this page via <see cref="AppNav.ToRoot"/>.
    /// </summary>
    public sealed partial class ShellPage : Page
    {
        public const string SectionChats = "chats";
        public const string SectionContacts = "contacts";
        public const string SectionMoments = "moments";
        public const string SectionMe = "me";

        private string _section = SectionChats;

        public ShellPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var section = e.Parameter as string;
            if (string.IsNullOrEmpty(section)) section = SectionChats;
            ShowSection(section);
        }

        public void ShowSection(string section)
        {
            if (string.IsNullOrEmpty(section)) section = SectionChats;
            _section = section;
            RootSplit.IsPaneOpen = false;

            switch (section)
            {
                case SectionContacts:
                    TitleText.Text = "联系人";
                    HighlightNav(NavContacts);
                    ContentFrame.Navigate(typeof(MainPage), SectionContacts);
                    break;
                case SectionMoments:
                    TitleText.Text = "动态";
                    HighlightNav(NavMoments);
                    ContentFrame.Navigate(typeof(MomentsPage));
                    break;
                case SectionMe:
                    TitleText.Text = "我";
                    HighlightNav(NavMe);
                    ContentFrame.Navigate(typeof(ProfilePage));
                    break;
                default:
                    TitleText.Text = "消息";
                    HighlightNav(NavChats);
                    ContentFrame.Navigate(typeof(MainPage), SectionChats);
                    break;
            }
        }

        private void HighlightNav(AppBarButton active)
        {
            foreach (var b in new[] { NavChats, NavContacts, NavMoments, NavMe })
            {
                if (b == null) continue;
                b.Foreground = b == active
                    ? (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["MetroAccentBrush"]
                    : (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["MetroPrimaryTextBrush"];
            }
        }

        private void Hamburger_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = !RootSplit.IsPaneOpen;
        }

        private void NavChats_Click(object sender, RoutedEventArgs e) => ShowSection(SectionChats);
        private void NavContacts_Click(object sender, RoutedEventArgs e) => ShowSection(SectionContacts);
        private void NavMoments_Click(object sender, RoutedEventArgs e) => ShowSection(SectionMoments);
        private void NavMe_Click(object sender, RoutedEventArgs e) => ShowSection(SectionMe);

        private void MenuSearch_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            AppNav.ToRoot(typeof(SearchPage));
        }

        private void MenuGateway_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            AppNav.ToRoot(typeof(AccountLoginPage));
        }

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            AppNav.ToRoot(typeof(SettingsPage));
        }

        private void MenuMarkAllRead_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            RelayToMain("MarkAllRead");
        }

        private void MenuMuteAll_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            RelayToMain("MuteAll");
        }

        private void MenuUnmuteAll_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            RelayToMain("UnmuteAll");
        }

        private void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            RootSplit.IsPaneOpen = false;
            RelayToMain("Refresh");
        }

        private void RelayToMain(string action)
        {
            if (!(ContentFrame.Content is MainPage main))
            {
                ShowSection(SectionChats);
                main = ContentFrame.Content as MainPage;
            }
            main?.HandleShellAction(action);
        }
    }
}
