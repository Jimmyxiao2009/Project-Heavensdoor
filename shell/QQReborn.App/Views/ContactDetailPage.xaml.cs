using System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ContactDetailPage : Page
    {
        private Contact _contact;
        private ContactDetailViewModel _vm;

        public ContactDetailPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is Contact contact)
            {
                _contact = contact;
                _vm = new ContactDetailViewModel(contact);
                DataContext = _vm;
                UpdateRemarkLabel();

                // Mock data already carries Gender/Age/Location, so there's nothing to
                // backfill there -- only remote mode needs the extra round-trip (getContacts
                // doesn't return those fields).
                if (AppServices.Gateway is IGatewayService remote)
                {
                    try
                    {
                        var profile = await remote.GetUserProfileAsync(contact.Uin);
                        _vm.ApplyProfile(profile);
                    }
                    catch (Exception)
                    {
                        // Leave the detail block showing "暂无更多资料" -- honest empty state,
                        // no crash on a flaky/unreachable backend.
                    }

                    try
                    {
                        var status = await remote.GetUserStatusTextAsync(contact.Uin);
                        if (!string.IsNullOrEmpty(status))
                            contact.Online = status == "在线" || status == "Q我吧" || status == "忙碌" || status == "离开";
                        var likes = await remote.GetProfileLikeCountAsync(contact.Uin);
                        if (likes > 0)
                            _vm.ApplyExtraHint("获赞 " + likes + (string.IsNullOrEmpty(status) ? "" : " · " + status));
                        else if (!string.IsNullOrEmpty(status))
                            _vm.ApplyExtraHint(status);
                    }
                    catch { }
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;
            var conv = new ChatConversation
            {
                // Must match the "f{uin}" scheme RemoteChatService/RealServer actually route
                // sends by -- this used to be an ad-hoc "contact-{uin}" placeholder that only
                // ever worked against MockChatService (which doesn't care about id format).
                Id = "f" + _contact.Uin,
                Kind = ConversationKind.Friend,
                Title = _contact.DisplayName,
                AvatarPath = _contact.AvatarPath
            };
            Frame.Navigate(typeof(ConversationPage), conv);
        }

        private void UpdateRemarkLabel()
        {
            if (_contact == null)
            {
                RemarkText.Text = "未设置";
                return;
            }
            RemarkText.Text = string.IsNullOrEmpty(_contact.Remark) ? "未设置" : _contact.Remark;
        }

        private async void RemarkRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_contact == null) return;
            var remote = AppServices.Gateway;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持设置备注。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var input = new TextBox
            {
                Text = _contact.Remark ?? "",
                PlaceholderText = "备注名",
                Margin = new Thickness(0, 12, 0, 0),
            };
            var dialog = new ContentDialog
            {
                Title = "设置备注",
                Content = input,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            var remark = (input.Text ?? "").Trim();
            try
            {
                var ok = await remote.SetFriendRemarkAsync(_contact.Uin, remark);
                if (ok)
                {
                    _contact.Remark = remark;
                    UpdateRemarkLabel();
                    _vm = new ContactDetailViewModel(_contact);
                    DataContext = _vm;
                    try { await new MessageDialog("备注已更新", "提示").ShowAsync(); }
                    catch { }
                }
                else
                {
                    try { await new MessageDialog("设置备注失败", "提示").ShowAsync(); }
                    catch { }
                }
            }
            catch
            {
                try { await new MessageDialog("设置备注失败，请检查网络", "提示").ShowAsync(); }
                catch { }
            }
        }

        private async void DeleteFriend_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;
            var remote = AppServices.Gateway;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持删除好友。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var name = _contact.DisplayName ?? _contact.Uin.ToString();
            var bothBox = new CheckBox { Content = "同时从对方好友列表删除", Margin = new Thickness(0, 10, 0, 0) };
            var blockBox = new CheckBox { Content = "加入黑名单", Margin = new Thickness(0, 6, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "确定删除好友 " + name + " 吗？",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(bothBox);
            panel.Children.Add(blockBox);

            var dialog = new ContentDialog
            {
                Title = "删除好友",
                Content = panel,
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            try
            {
                var ok = await remote.DeleteFriendAsync(
                    _contact.Uin,
                    tempBlock: blockBox.IsChecked == true,
                    bothDel: bothBox.IsChecked == true);
                if (ok)
                {
                    if (Frame.CanGoBack) Frame.GoBack();
                }
                else
                {
                    try { await new MessageDialog("删除失败", "提示").ShowAsync(); }
                    catch { }
                }
            }
            catch
            {
                try { await new MessageDialog("删除失败，请检查网络", "提示").ShowAsync(); }
                catch { }
            }
        }

        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;

            var remote = AppServices.Gateway;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持点赞。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            // QQ daily like cap is 10; offer a short preset instead of a free-form number.
            var combo = new ComboBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0,
            };
            combo.Items.Add(new ComboBoxItem { Content = "1 次", Tag = 1 });
            combo.Items.Add(new ComboBoxItem { Content = "5 次", Tag = 5 });
            combo.Items.Add(new ComboBoxItem { Content = "10 次（每日上限）", Tag = 10 });

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "给 " + (_contact.DisplayName ?? _contact.Uin.ToString()) + " 的名片点赞",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(combo);

            var dialog = new ContentDialog
            {
                Title = "名片点赞",
                Content = panel,
                PrimaryButtonText = "点赞",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            var count = (combo.SelectedItem as ComboBoxItem)?.Tag is int n ? n : 1;
            LikeButton.IsEnabled = false;
            try
            {
                var ok = await remote.SendLikeAsync(_contact.Uin, count);
                try
                {
                    await new MessageDialog(
                        ok ? $"已点赞 {count} 次" : "点赞失败（今日可能已达上限）",
                        "提示").ShowAsync();
                }
                catch { }
            }
            catch
            {
                try { await new MessageDialog("点赞失败，请检查网络连接", "提示").ShowAsync(); }
                catch { }
            }
            finally
            {
                LikeButton.IsEnabled = true;
            }
        }
    }
}
