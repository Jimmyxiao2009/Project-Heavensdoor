using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    public sealed partial class GroupInfoPage : Page
    {
        /// <summary>Above this many members, the grid collapses to a preview and shows an
        /// "展开全部" affordance instead of dumping everyone on screen at once.</summary>
        private const int CollapseThreshold = 20;

        private readonly ObservableCollection<GroupMember> _members = new ObservableCollection<GroupMember>();
        private IReadOnlyList<GroupMember> _allMembers = Array.Empty<GroupMember>();
        private ChatConversation _conversation;
        /// <summary>Suppress ToggleSwitch.Toggled while we stamp initial IsOn from the model.</summary>
        private bool _suppressToggleEvents;

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
            // Backend (fake or real) supplies the real announcement via getConversations;
            // client logic is the same for both -- just show it, or an honest empty state.
            AnnouncementText.Text = string.IsNullOrEmpty(conv.Announcement) ? "暂无群公告" : conv.Announcement;

            _suppressToggleEvents = true;
            PinToggle.IsOn = conv.IsPinned;
            MuteToggle.IsOn = conv.IsMuted;
            _suppressToggleEvents = false;

            if (!string.IsNullOrEmpty(conv.AvatarPath))
            {
                try { GroupAvatar.ImageSource = new BitmapImage(new Uri(conv.AvatarPath)); }
                catch (Exception) { }
            }

            try
            {
                MemberCountText.Text = "加载成员中…";
                var members = await App.ChatService.GetGroupMembersAsync(conv.Id);
                _allMembers = members != null
                    ? members
                        .OrderBy(m => m != null && m.IsOwner ? 0 : (m != null && m.Role == "管理员" ? 1 : 2))
                        .ThenBy(m => m != null ? (m.Name ?? "") : "", StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<GroupMember>();
                _members.Clear();
                ExpandMembersButton.Visibility = Visibility.Collapsed;

                if (_allMembers.Count > CollapseThreshold)
                {
                    foreach (var m in _allMembers.Take(CollapseThreshold)) _members.Add(m);
                    ExpandMembersButton.Content = $"展开全部（共 {_allMembers.Count} 人）";
                    ExpandMembersButton.Visibility = Visibility.Visible;
                }
                else
                {
                    foreach (var m in _allMembers) _members.Add(m);
                }

                MemberCountText.Text = _allMembers.Count == 0
                    ? "暂无成员（请确认 NapCat 在线）"
                    : "共 " + _allMembers.Count + " 名成员";
            }
            catch (Exception ex)
            {
                MemberCountText.Text = "成员加载失败";
                System.Diagnostics.Debug.WriteLine("GroupInfo members: " + ex);
            }
        }

        private void ExpandMembersButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in _allMembers.Skip(_members.Count)) _members.Add(m);
            ExpandMembersButton.Visibility = Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame.CanGoBack) this.Frame.GoBack();
        }

        private async void PinToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents || _conversation == null) return;
            var next = PinToggle.IsOn;
            var prev = _conversation.IsPinned;
            _conversation.IsPinned = next;
            try { await App.ChatService.SetConversationFlagsAsync(_conversation.Id, next, null); }
            catch
            {
                _suppressToggleEvents = true;
                _conversation.IsPinned = prev;
                PinToggle.IsOn = prev;
                _suppressToggleEvents = false;
            }
        }

        private async void MuteToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents || _conversation == null) return;
            var next = MuteToggle.IsOn;
            var updated = await ConversationNotificationSettings.TrySetMutedAsync(App.ChatService, _conversation, next);
            if (!updated)
            {
                _suppressToggleEvents = true;
                MuteToggle.IsOn = _conversation.IsMuted;
                _suppressToggleEvents = false;
            }
        }

        private async void LeaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_conversation == null) return;

            IUICommand chosen;
            var dialog = new MessageDialog("确定要退出该群聊吗？", "退出群聊");
            var leaveCommand = new UICommand("退出");
            dialog.Commands.Add(leaveCommand);
            dialog.Commands.Add(new UICommand("取消"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            try { chosen = await dialog.ShowAsync(); }
            catch (Exception) { return; }

            if (chosen != leaveCommand) return;

            if (App.ChatService is RemoteChatService remote)
            {
                try
                {
                    var left = await remote.QuitGroupAsync(_conversation.Id);
                    if (left)
                    {
                        if (this.Frame.CanGoBack) this.Frame.GoBack();
                        return;
                    }

                    var failDialog = new MessageDialog("退群失败，请稍后重试。", "退出群聊");
                    try { await failDialog.ShowAsync(); }
                    catch (Exception) { }
                }
                catch (Exception)
                {
                    var errorDialog = new MessageDialog("退群失败，请检查网络连接后重试。", "退出群聊");
                    try { await errorDialog.ShowAsync(); }
                    catch (Exception) { }
                }
            }
            else
            {
                // Mock backend: no real group membership to mutate, so just say so
                // honestly instead of silently pretending the leave happened.
                var demoDialog = new MessageDialog("演示模式不支持退出群聊。", "提示");
                try { await demoDialog.ShowAsync(); }
                catch (Exception) { }
            }
        }

        private async void GroupName_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation == null) return;
            var remote = App.ChatService as RemoteChatService;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持修改群名。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var input = new TextBox { Text = _conversation.Title, Margin = new Thickness(0, 16, 0, 0) };
            var dialog = new ContentDialog
            {
                Title = "修改群名称",
                Content = input,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消"
            };

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var newName = input.Text.Trim();
                try
                {
                    var ok = await remote.GroupRenameAsync(_conversation.Id, newName);
                    if (ok)
                    {
                        _conversation.Title = newName;
                        GroupTitle.Text = newName;
                        GroupNameValue.Text = newName;
                    }
                    else
                    {
                        await new MessageDialog("修改失败", "提示").ShowAsync();
                    }
                }
                catch
                {
                    await new MessageDialog("修改失败，请检查网络连接", "提示").ShowAsync();
                }
            }
        }

        private void MemberGrid_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var elt = sender as FrameworkElement;
            if (elt != null)
            {
                Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(elt.Parent as FrameworkElement);
            }
        }

        private void MemberGrid_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == Windows.UI.Input.HoldingState.Started)
            {
                var elt = sender as FrameworkElement;
                if (elt != null)
                {
                    Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(elt.Parent as FrameworkElement);
                }
            }
        }

        private async void ChangeMemberName_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var member = menu?.DataContext as GroupMember;
            if (member == null || _conversation == null) return;

            var remote = App.ChatService as RemoteChatService;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持修改名片。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var input = new TextBox { Text = member.Name, Margin = new Thickness(0, 16, 0, 0) };
            var dialog = new ContentDialog
            {
                Title = "修改群名片",
                Content = input,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消"
            };

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var newName = input.Text.Trim();
                try
                {
                    var ok = await remote.GroupMemberRenameAsync(_conversation.Id, member.Uin, newName);
                    if (ok)
                    {
                        member.Name = newName;
                        // Reload members to refresh view, or let ObservableCollection handle if INotifyPropertyChanged is there
                        var m = _allMembers.FirstOrDefault(x => x.Uin == member.Uin);
                        if (m != null) m.Name = newName;
                        // Forcing refresh
                        MemberGrid.ItemsSource = null;
                        MemberGrid.ItemsSource = _members;
                    }
                    else
                    {
                        await new MessageDialog("修改失败", "提示").ShowAsync();
                    }
                }
                catch
                {
                    await new MessageDialog("修改失败，请检查网络连接", "提示").ShowAsync();
                }
            }
        }

        private async void SetSpecialTitle_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var member = menu?.DataContext as GroupMember;
            if (member == null || _conversation == null) return;

            var remote = App.ChatService as RemoteChatService;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持设置专属头衔。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var input = new TextBox { PlaceholderText = "输入新头衔", Margin = new Thickness(0, 16, 0, 0) };
            var dialog = new ContentDialog
            {
                Title = "设置专属头衔",
                Content = input,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消"
            };

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                var newTitle = input.Text.Trim();
                try
                {
                    var ok = await remote.GroupSetSpecialTitleAsync(_conversation.Id, member.Uin, newTitle);
                    if (ok)
                    {
                        await new MessageDialog("设置成功", "提示").ShowAsync();
                    }
                    else
                    {
                        await new MessageDialog("设置失败", "提示").ShowAsync();
                    }
                }
                catch
                {
                    await new MessageDialog("设置失败，请检查网络连接", "提示").ShowAsync();
                }
            }
        }
    }
}
