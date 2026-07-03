using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace QQReborn.App.Views
{
    /// <summary>
    /// 加好友 / 加群. Self-contained, purely local fake search:
    /// type a QQ/group number or nickname, tap 搜索 to surface a fabricated
    /// result card, then 加好友 flips to "已发送申请" (no service involved).
    /// </summary>
    public sealed partial class AddFriendPage : Page
    {
        public AddFriendPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            QueryBox.Focus(FocusState.Programmatic);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                DoSearch();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            DoSearch();
        }

        private void DoSearch()
        {
            var query = (QueryBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(query))
            {
                ResultCard.Visibility = Visibility.Collapsed;
                IdleHint.Visibility = Visibility.Visible;
                // Empty query is not a "no results" case; show only the idle hint.
                EmptyHint.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            IdleHint.Visibility = Visibility.Collapsed;

            // Fabricate a nickname & uin from the query. Pure digits -> a number lookup,
            // otherwise treat the text itself as the nickname.
            bool allDigits = IsAllDigits(query);
            ResultName.Text = allDigits ? ("QQ用户_" + query) : query;
            ResultUin.Text = "QQ: " + (allDigits ? query : FakeUinFor(query));

            // Reset the add button to its initial state for every fresh search.
            AddButton.Content = "加好友";
            AddButton.IsEnabled = true;
            AddButton.Background = (Brush)Application.Current.Resources["MetroAccentBrush"];
            SentHint.Visibility = Visibility.Collapsed;

            ResultCard.Visibility = Visibility.Visible;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // Purely local: flip to a sent state.
            AddButton.Content = "已发送申请";
            AddButton.IsEnabled = false;
            AddButton.Background = (Brush)Application.Current.Resources["MetroSubtleBrush"];
            SentHint.Visibility = Visibility.Visible;
        }

        private static bool IsAllDigits(string s)
        {
            foreach (var c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>Stable fabricated 8-digit uin derived from a non-numeric query.</summary>
        private static string FakeUinFor(string query)
        {
            int hash = 0;
            foreach (var c in query) hash = (hash * 31 + c) & 0x7FFFFFFF;
            return (10000000 + hash % 90000000).ToString();
        }
    }
}
