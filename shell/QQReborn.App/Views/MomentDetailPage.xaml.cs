using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using QQReborn.App.Models;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    public sealed partial class MomentDetailPage : Page
    {
        private readonly IMomentsService _moments;
        private Moment _moment;

        public MomentDetailPage()
        {
            InitializeComponent();
            _moments = App.ChatService is RemoteChatService remote
                ? (IMomentsService)new RemoteMomentsService(remote)
                : new MockMomentsService();
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _moment = e.Parameter as Moment;
            DataContext = _moment;
            if (_moment != null) LikeButton.Content = _moment.IsLiked ? "取消赞" : "赞";
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void Like_Click(object sender, RoutedEventArgs e)
        {
            if (_moment == null) return;
            await _moments.ToggleLikeAsync(_moment);
            LikeButton.Content = _moment.IsLiked ? "取消赞" : "赞";
        }

        private async void SendComment_Click(object sender, RoutedEventArgs e)
        {
            if (_moment == null || string.IsNullOrWhiteSpace(CommentBox.Text)) return;
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            try
            {
                await _moments.AddCommentAsync(_moment, CommentBox.Text);
                CommentBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.Message, "动态评论").ShowAsync();
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }
    }
}
