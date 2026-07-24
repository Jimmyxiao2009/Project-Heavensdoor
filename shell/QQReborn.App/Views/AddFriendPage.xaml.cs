using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    /// <summary>
    /// 加好友 / 加群.
    /// Remote mode: a pure-digit query does a real GetUserProfileAsync(uin) lookup against
    /// the backend; anything else is honestly reported as unsupported (LagrangeV2 has no
    /// nickname-search API), and the 加好友 button stays disabled because there is no
    /// send-friend-request API either -- tapping it must never pretend a request went out.
    /// Mock mode keeps the original self-contained fabricated demo (no service involved),
    /// since MockChatService doesn't have a user directory to search against.
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
            ResultCard.Visibility = Visibility.Collapsed;

            bool allDigits = IsAllDigits(query);

            if (App.ChatService is RemoteChatService remote)
            {
                if (!allDigits || !long.TryParse(query, out var uin))
                {
                    // The real protocol has no nickname-search API -- say so instead of
                    // fabricating a match like the old local-only version did. A digit
                    // string too long to fit in a long (bad uin, not a nickname) lands
                    // here too rather than throwing.
                    ShowEmptyHint("请输入QQ号搜索（协议不支持昵称搜索）");
                    return;
                }

                var ignore = SearchRemoteAsync(remote, uin);
                return;
            }

            // Mock mode: no user directory to query against, so keep the original
            // fabricated demo card purely as a UI preview (clearly a local invention,
            // not a real lookup -- there is no MockChatService.GetUserProfileAsync).
            ResultName.Text = allDigits ? ("QQ用户_" + query) : query;
            ResultUin.Text = "QQ: " + (allDigits ? query : FakeUinFor(query));
            ResultSignature.Text = "（演示数据，非真实查询结果）";

            AddButton.Content = "加好友";
            AddButton.IsEnabled = true;
            AddButton.Background = (Brush)Application.Current.Resources["MetroAccentBrush"];
            SentHint.Visibility = Visibility.Collapsed;

            ResultCard.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task SearchRemoteAsync(RemoteChatService remote, long uin)
        {
            try
            {
                var profile = await remote.GetUserProfileAsync(uin);
                if (profile == null || profile.Uin == 0)
                {
                    ShowEmptyHint("未找到该用户");
                    return;
                }

                ResultName.Text = string.IsNullOrEmpty(profile.Nickname) ? ("QQ: " + profile.Uin) : profile.Nickname;
                ResultUin.Text = "QQ: " + profile.Uin;
                ResultSignature.Text = string.IsNullOrEmpty(profile.Signature)
                    ? "Lv." + profile.Level
                    : profile.Signature + "  ·  Lv." + profile.Level;

                // LagrangeV2 has no "send friend request" API -- never pretend the tap
                // succeeded. Disabled + explanatory label, same honesty convention as
                // FriendRequestsPage's "同意请求" handling.
                AddButton.Content = "暂不支持发送好友申请";
                AddButton.IsEnabled = false;
                AddButton.Background = (Brush)Application.Current.Resources["MetroSubtleBrush"];
                SentHint.Visibility = Visibility.Collapsed;

                ResultCard.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                ShowEmptyHint("未找到该用户");
            }
        }

        private void ShowEmptyHint(string message)
        {
            ResultCard.Visibility = Visibility.Collapsed;
            IdleHint.Visibility = Visibility.Collapsed;
            EmptyHint.Text = message;
            EmptyHint.Visibility = Visibility.Visible;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.ChatService is RemoteChatService)
            {
                // Button is disabled in remote mode (see SearchRemoteAsync) -- this is a
                // defensive no-op, not a real path, since there's no API to call.
                return;
            }

            // Mock mode only: purely local, flips to a sent state for the demo.
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

        /// <summary>Stable fabricated 8-digit uin derived from a non-numeric query (mock mode only).</summary>
        private static string FakeUinFor(string query)
        {
            int hash = 0;
            foreach (var c in query) hash = (hash * 31 + c) & 0x7FFFFFFF;
            return (10000000 + hash % 90000000).ToString();
        }
    }
}
