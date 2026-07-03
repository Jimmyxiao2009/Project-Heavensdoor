using System;
using System.Collections.ObjectModel;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;

namespace QQReborn.App.Views
{
    public sealed partial class GroupInfoPage : Page
    {
        private readonly ObservableCollection<GroupMember> _members = new ObservableCollection<GroupMember>();
        private ChatConversation _conversation;

        public GroupInfoPage()
        {
            InitializeComponent();
            MemberGrid.ItemsSource = _members;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!(e.Parameter is ChatConversation conv)) return;
            _conversation = conv;

            GroupTitle.Text = conv.Title;
            GroupNameValue.Text = conv.Title;
            AnnouncementText.Text = "欢迎加入本群，请文明聊天，禁止发布广告和违规内容。有问题请联系群主或管理员。";

            if (!string.IsNullOrEmpty(conv.AvatarPath))
            {
                try { GroupAvatar.ImageSource = new BitmapImage(new Uri(conv.AvatarPath)); }
                catch (Exception) { }
            }

            try
            {
                var members = await App.ChatService.GetGroupMembersAsync(conv.Id);
                _members.Clear();
                if (members != null)
                {
                    foreach (var m in members) _members.Add(m);
                }
                MemberCountText.Text = "共 " + _members.Count + " 名成员";
            }
            catch (Exception)
            {
                MemberCountText.Text = "共 0 名成员";
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame.CanGoBack) this.Frame.GoBack();
        }

        private async void LeaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Stub: confirm-only, no backend leave action wired yet.
            var dialog = new MessageDialog("确定要退出该群聊吗？", "退出群聊");
            dialog.Commands.Add(new UICommand("退出"));
            dialog.Commands.Add(new UICommand("取消"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            try { await dialog.ShowAsync(); }
            catch (Exception) { }
        }
    }
}
