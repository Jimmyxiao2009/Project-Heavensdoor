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
        private string _latestNoticeId;
        private long _selfUin;
        private bool _selfIsAdmin;
        private bool _selfIsOwner;

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

            // Refresh announcement from NapCat _get_group_notice when available.
            if (App.ChatService is RemoteChatService noticeRemote)
            {
                try
                {
                    var notices = await noticeRemote.GetGroupNoticesAsync(conv.Id);
                    if (notices != null && notices.Count > 0 && !string.IsNullOrEmpty(notices[0].Content))
                    {
                        AnnouncementText.Text = notices[0].Content;
                        conv.Announcement = notices[0].Content;
                        _latestNoticeId = notices[0].Id;
                    }
                }
                catch (Exception) { /* keep memo fallback */ }
            }

            try
            {
                MemberCountText.Text = "加载成员中…";
                // Resolve self uin first so we can map role from the member list.
                try
                {
                    var self = await App.ChatService.GetSelfAsync();
                    _selfUin = self != null ? self.Uin : 0;
                }
                catch { _selfUin = 0; }

                var members = await App.ChatService.GetGroupMembersAsync(conv.Id);
                _allMembers = members != null
                    ? members
                        .OrderBy(m => m != null && m.IsOwner ? 0 : (m != null && m.IsAdmin ? 1 : 2))
                        .ThenBy(m => m != null ? (m.Name ?? "") : "", StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<GroupMember>();
                ResolveSelfRole();
                ApplyAdminVisibility();

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
                    : "共 " + _allMembers.Count + " 名成员"
                      + (_selfIsOwner ? " · 你是群主" : (_selfIsAdmin ? " · 你是管理员" : ""));
            }
            catch (Exception ex)
            {
                MemberCountText.Text = "成员加载失败";
                System.Diagnostics.Debug.WriteLine("GroupInfo members: " + ex);
                ApplyAdminVisibility();
            }
        }

        private void ResolveSelfRole()
        {
            _selfIsAdmin = false;
            _selfIsOwner = false;
            if (_selfUin <= 0 || _allMembers == null) return;
            var me = _allMembers.FirstOrDefault(m => m != null && m.Uin == _selfUin);
            if (me == null) return;
            _selfIsOwner = me.IsOwner;
            _selfIsAdmin = me.IsAdmin;
        }

        private void ApplyAdminVisibility()
        {
            var adminVis = _selfIsAdmin ? Visibility.Visible : Visibility.Collapsed;
            if (AnnouncementEditLabel != null)
                AnnouncementEditLabel.Visibility = adminVis;
            if (ShutListRow != null) ShutListRow.Visibility = adminVis;
            if (PortraitRow != null) PortraitRow.Visibility = adminVis;
            if (WholeBanRow != null) WholeBanRow.Visibility = adminVis;
            if (GroupNameEditGlyph != null)
                GroupNameEditGlyph.Visibility = adminVis;
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
            if (!_selfIsAdmin)
            {
                // Ordinary members: name row is display-only.
                return;
            }
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
            ShowMemberMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void MemberGrid_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            ShowMemberMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowMemberMenu(FrameworkElement anchor)
        {
            if (anchor == null) return;
            var member = anchor.DataContext as GroupMember;
            if (member == null || _conversation == null) return;
            // Don't manage yourself via this menu.
            if (_selfUin > 0 && member.Uin == _selfUin) return;
            // Ordinary members: no management menu.
            if (!_selfIsAdmin) return;

            var menu = new MenuFlyout();

            var card = new MenuFlyoutItem { Text = "修改群名片", DataContext = member };
            card.Click += ChangeMemberName_Click;
            menu.Items.Add(card);

            if (_selfIsOwner)
            {
                var title = new MenuFlyoutItem { Text = "设置专属头衔", DataContext = member };
                title.Click += SetSpecialTitle_Click;
                menu.Items.Add(title);

                if (!member.IsOwner)
                {
                    if (member.Role == "管理员")
                    {
                        var unset = new MenuFlyoutItem { Text = "取消管理员", DataContext = member };
                        unset.Click += UnsetAdmin_Click;
                        menu.Items.Add(unset);
                    }
                    else
                    {
                        var set = new MenuFlyoutItem { Text = "设为管理员", DataContext = member };
                        set.Click += SetAdmin_Click;
                        menu.Items.Add(set);
                    }
                }
            }

            // Admins cannot ban/kick the owner; non-owner admins shouldn't manage other admins.
            if (!member.IsOwner && (_selfIsOwner || !member.IsAdmin))
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var ban = new MenuFlyoutItem { Text = "禁言…", DataContext = member };
                ban.Click += BanMember_Click;
                menu.Items.Add(ban);
                var unban = new MenuFlyoutItem { Text = "解除禁言", DataContext = member };
                unban.Click += UnbanMember_Click;
                menu.Items.Add(unban);
                var kick = new MenuFlyoutItem { Text = "踢出本群", DataContext = member };
                kick.Click += KickMember_Click;
                menu.Items.Add(kick);
            }

            if (menu.Items.Count == 0) return;
            menu.ShowAt(anchor);
        }

        private async void ChangeMemberName_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var member = menu?.DataContext as GroupMember;
            if (member == null || _conversation == null) return;
            if (!_selfIsAdmin) return;

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
            if (!_selfIsOwner) return;

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

        private async void SetAdmin_Click(object sender, RoutedEventArgs e)
        {
            await SetAdminInternalAsync(sender, enable: true);
        }

        private async void UnsetAdmin_Click(object sender, RoutedEventArgs e)
        {
            await SetAdminInternalAsync(sender, enable: false);
        }

        private async System.Threading.Tasks.Task SetAdminInternalAsync(object sender, bool enable)
        {
            var member = GetMemberFromMenu(sender);
            if (member == null || _conversation == null) return;
            if (!_selfIsOwner)
            {
                await ShowHintAsync("仅群主可设置管理员。");
                return;
            }
            if (member.IsOwner)
            {
                await ShowHintAsync("不能对群主执行该操作。");
                return;
            }

            var remote = await RequireRemoteAsync("演示模式不支持设置管理员。");
            if (remote == null) return;

            var label = enable ? "设为管理员" : "取消管理员";
            var confirm = await ConfirmAsync($"确定将 {DisplayName(member)} {label}吗？", label);
            if (!confirm) return;

            try
            {
                var ok = await remote.SetGroupAdminAsync(_conversation.Id, member.Uin, enable);
                if (ok)
                {
                    member.Role = enable ? "管理员" : "";
                    RefreshMemberGrid();
                    await ShowHintAsync(enable ? "已设为管理员" : "已取消管理员");
                }
                else
                    await ShowHintAsync("操作失败（权限不足或网络错误）");
            }
            catch
            {
                await ShowHintAsync("操作失败，请检查网络连接");
            }
        }

        private async void BanMember_Click(object sender, RoutedEventArgs e)
        {
            var member = GetMemberFromMenu(sender);
            if (member == null || _conversation == null) return;
            if (!_selfIsAdmin)
            {
                await ShowHintAsync("仅群主/管理员可禁言。");
                return;
            }
            if (member.IsOwner)
            {
                await ShowHintAsync("不能禁言群主。");
                return;
            }
            if (!_selfIsOwner && member.IsAdmin)
            {
                await ShowHintAsync("不能禁言其他管理员。");
                return;
            }

            var remote = await RequireRemoteAsync("演示模式不支持禁言。");
            if (remote == null) return;

            var combo = new ComboBox
            {
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 1,
            };
            combo.Items.Add(new ComboBoxItem { Content = "10 分钟", Tag = 10 * 60 });
            combo.Items.Add(new ComboBoxItem { Content = "1 小时", Tag = 60 * 60 });
            combo.Items.Add(new ComboBoxItem { Content = "1 天", Tag = 24 * 60 * 60 });
            combo.Items.Add(new ComboBoxItem { Content = "7 天", Tag = 7 * 24 * 60 * 60 });
            combo.Items.Add(new ComboBoxItem { Content = "30 天", Tag = 30 * 24 * 60 * 60 });

            var dialog = new ContentDialog
            {
                Title = "禁言 " + DisplayName(member),
                Content = combo,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            var item = combo.SelectedItem as ComboBoxItem;
            var seconds = item?.Tag is int s ? s : 60 * 60;

            try
            {
                var ok = await remote.SetGroupBanAsync(_conversation.Id, member.Uin, seconds);
                await ShowHintAsync(ok ? "已禁言" : "禁言失败（权限不足或网络错误）");
            }
            catch
            {
                await ShowHintAsync("禁言失败，请检查网络连接");
            }
        }

        private async void UnbanMember_Click(object sender, RoutedEventArgs e)
        {
            var member = GetMemberFromMenu(sender);
            if (member == null || _conversation == null) return;
            if (!_selfIsAdmin) return;

            var remote = await RequireRemoteAsync("演示模式不支持解除禁言。");
            if (remote == null) return;

            try
            {
                var ok = await remote.SetGroupBanAsync(_conversation.Id, member.Uin, 0);
                await ShowHintAsync(ok ? "已解除禁言" : "解除失败（权限不足或网络错误）");
            }
            catch
            {
                await ShowHintAsync("解除失败，请检查网络连接");
            }
        }

        private async void KickMember_Click(object sender, RoutedEventArgs e)
        {
            var member = GetMemberFromMenu(sender);
            if (member == null || _conversation == null) return;
            if (!_selfIsAdmin)
            {
                await ShowHintAsync("仅群主/管理员可踢人。");
                return;
            }
            if (member.IsOwner)
            {
                await ShowHintAsync("不能踢出群主。");
                return;
            }
            if (!_selfIsOwner && member.IsAdmin)
            {
                await ShowHintAsync("不能踢出其他管理员。");
                return;
            }

            var remote = await RequireRemoteAsync("演示模式不支持踢人。");
            if (remote == null) return;

            var rejectBox = new CheckBox
            {
                Content = "拒绝此人再次加群",
                Margin = new Thickness(0, 12, 0, 0),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = $"确定将 {DisplayName(member)} 踢出本群吗？",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(rejectBox);

            var dialog = new ContentDialog
            {
                Title = "踢出成员",
                Content = panel,
                PrimaryButtonText = "踢出",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            try
            {
                var ok = await remote.SetGroupKickAsync(
                    _conversation.Id, member.Uin, rejectBox.IsChecked == true);
                if (ok)
                {
                    RemoveMemberLocal(member.Uin);
                    await ShowHintAsync("已踢出");
                }
                else
                    await ShowHintAsync("踢出失败（权限不足或网络错误）");
            }
            catch
            {
                await ShowHintAsync("踢出失败，请检查网络连接");
            }
        }

        private async void Announcement_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation == null) return;
            if (!_selfIsAdmin)
            {
                // Read-only for members: show full text if truncated, no edit.
                var text = AnnouncementText?.Text;
                if (!string.IsNullOrEmpty(text) && text != "暂无群公告")
                    await ShowHintAsync(text);
                return;
            }
            var remote = await RequireRemoteAsync("演示模式不支持编辑群公告。");
            if (remote == null) return;

            var input = new TextBox
            {
                Text = _conversation.Announcement ?? "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 140,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var dialog = new ContentDialog
            {
                Title = "编辑群公告",
                Content = input,
                PrimaryButtonText = "发布",
                CloseButtonText = "取消",
            };
            if (!string.IsNullOrEmpty(_latestNoticeId))
                dialog.SecondaryButtonText = "删除当前";

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }

            if (res == ContentDialogResult.Primary)
            {
                var text = (input.Text ?? "").Trim();
                if (string.IsNullOrEmpty(text))
                {
                    await ShowHintAsync("公告内容不能为空");
                    return;
                }
                try
                {
                    var ok = await remote.SendGroupNoticeAsync(_conversation.Id, text);
                    if (ok)
                    {
                        _conversation.Announcement = text;
                        AnnouncementText.Text = text;
                        await ShowHintAsync("公告已发布");
                    }
                    else await ShowHintAsync("发布失败（权限不足或网络错误）");
                }
                catch { await ShowHintAsync("发布失败，请检查网络连接"); }
            }
            else if (res == ContentDialogResult.Secondary && !string.IsNullOrEmpty(_latestNoticeId))
            {
                try
                {
                    var ok = await remote.DeleteGroupNoticeAsync(_conversation.Id, _latestNoticeId);
                    if (ok)
                    {
                        _latestNoticeId = null;
                        _conversation.Announcement = "";
                        AnnouncementText.Text = "暂无群公告";
                        await ShowHintAsync("已删除公告");
                    }
                    else await ShowHintAsync("删除失败（权限不足或网络错误）");
                }
                catch { await ShowHintAsync("删除失败，请检查网络连接"); }
            }
        }

        private void GroupFiles_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation == null) return;
            if (!(App.ChatService is RemoteChatService))
            {
                var _ = ShowHintAsync("演示模式不支持群文件。");
                return;
            }
            Frame.Navigate(typeof(GroupFilesPage), new GroupFilesPage.Args
            {
                ConversationId = _conversation.Id,
                Title = _conversation.Title,
            });
        }

        private async void GroupSign_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var remote = await RequireRemoteAsync("演示模式不支持群签到。");
            if (remote == null || _conversation == null) return;
            try
            {
                var ok = await remote.GroupSignAsync(_conversation.Id);
                await ShowHintAsync(ok ? "签到成功" : "签到失败");
            }
            catch { await ShowHintAsync("签到失败，请检查网络"); }
        }

        private async void GroupHonor_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var remote = await RequireRemoteAsync("演示模式不支持群荣誉。");
            if (remote == null || _conversation == null) return;
            try
            {
                var summary = await remote.GetGroupHonorSummaryAsync(_conversation.Id);
                if (string.IsNullOrWhiteSpace(summary) || summary == "null")
                    await ShowHintAsync("暂无群荣誉数据");
                else
                {
                    var text = summary.Length > 800 ? summary.Substring(0, 800) + "…" : summary;
                    await ShowHintAsync(text);
                }
            }
            catch { await ShowHintAsync("获取群荣誉失败"); }
        }

        private async void GroupEssence_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var remote = await RequireRemoteAsync("演示模式不支持精华消息。");
            if (remote == null || _conversation == null) return;
            try
            {
                var items = await remote.GetEssenceSummariesAsync(_conversation.Id);
                if (items == null || items.Count == 0)
                    await ShowHintAsync("暂无精华消息");
                else
                {
                    var body = string.Join("\n\n", items.Take(12));
                    await ShowHintAsync(body);
                }
            }
            catch { await ShowHintAsync("获取精华失败"); }
        }

        private async void GroupShutList_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (!_selfIsAdmin) return;
            var remote = await RequireRemoteAsync("演示模式不支持禁言列表。");
            if (remote == null || _conversation == null) return;
            try
            {
                var items = await remote.GetGroupShutListAsync(_conversation.Id);
                if (items == null || items.Count == 0)
                    await ShowHintAsync("当前无人被禁言");
                else
                    await ShowHintAsync(string.Join("\n", items.Take(30)));
            }
            catch { await ShowHintAsync("获取禁言列表失败"); }
        }

        private async void GroupRemark_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var remote = await RequireRemoteAsync("演示模式不支持群备注。");
            if (remote == null || _conversation == null) return;
            var input = new TextBox { Margin = new Thickness(0, 12, 0, 0), PlaceholderText = "群备注" };
            var dialog = new ContentDialog
            {
                Title = "设置群备注",
                Content = input,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
            };
            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;
            try
            {
                var ok = await remote.SetGroupRemarkAsync(_conversation.Id, (input.Text ?? "").Trim());
                await ShowHintAsync(ok ? "备注已保存" : "保存失败");
            }
            catch { await ShowHintAsync("保存失败"); }
        }

        private async void GroupPortrait_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (!_selfIsAdmin) return;
            var remote = await RequireRemoteAsync("演示模式不支持修改群头像。");
            if (remote == null || _conversation == null) return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
                var bytes = new byte[buffer.Length];
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    reader.ReadBytes(bytes);
                var b64 = Convert.ToBase64String(bytes);
                var ok = await remote.SetGroupPortraitAsync(_conversation.Id, b64);
                await ShowHintAsync(ok ? "群头像已更新" : "更新失败");
            }
            catch { await ShowHintAsync("更新群头像失败"); }
        }

        private async void WholeBan_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_conversation == null || !_selfIsAdmin) return;
            var remote = await RequireRemoteAsync("演示模式不支持全员禁言。");
            if (remote == null) return;

            var dialog = new ContentDialog
            {
                Title = "全员禁言",
                Content = "开启后仅群主和管理员可发言。",
                PrimaryButtonText = "开启",
                SecondaryButtonText = "解除",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }

            bool? enable = null;
            if (res == ContentDialogResult.Primary) enable = true;
            else if (res == ContentDialogResult.Secondary) enable = false;
            if (enable == null) return;

            try
            {
                var ok = await remote.SetGroupWholeBanAsync(_conversation.Id, enable.Value);
                await ShowHintAsync(ok
                    ? (enable.Value ? "已开启全员禁言" : "已解除全员禁言")
                    : "操作失败（权限不足或网络错误）");
            }
            catch
            {
                await ShowHintAsync("操作失败，请检查网络连接");
            }
        }

        private GroupMember GetMemberFromMenu(object sender)
        {
            var menu = sender as MenuFlyoutItem;
            return menu?.DataContext as GroupMember;
        }

        private async System.Threading.Tasks.Task<RemoteChatService> RequireRemoteAsync(string demoMessage)
        {
            var remote = App.ChatService as RemoteChatService;
            if (remote == null)
                await ShowHintAsync(demoMessage);
            return remote;
        }

        private static string DisplayName(GroupMember member)
        {
            if (member == null) return "";
            return string.IsNullOrEmpty(member.Name) ? member.Uin.ToString() : member.Name;
        }

        private async System.Threading.Tasks.Task<bool> ConfirmAsync(string message, string title)
        {
            var dialog = new MessageDialog(message, title);
            var ok = new UICommand("确定");
            dialog.Commands.Add(ok);
            dialog.Commands.Add(new UICommand("取消"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            try
            {
                var chosen = await dialog.ShowAsync();
                return chosen == ok;
            }
            catch
            {
                return false;
            }
        }

        private static async System.Threading.Tasks.Task ShowHintAsync(string message)
        {
            try { await new MessageDialog(message, "提示").ShowAsync(); }
            catch { }
        }

        private void RefreshMemberGrid()
        {
            MemberGrid.ItemsSource = null;
            MemberGrid.ItemsSource = _members;
        }

        private void RemoveMemberLocal(long uin)
        {
            for (var i = _members.Count - 1; i >= 0; i--)
            {
                if (_members[i] != null && _members[i].Uin == uin)
                    _members.RemoveAt(i);
            }

            if (_allMembers != null)
            {
                _allMembers = _allMembers.Where(m => m == null || m.Uin != uin).ToList();
                MemberCountText.Text = _allMembers.Count == 0
                    ? "暂无成员"
                    : "共 " + _allMembers.Count + " 名成员";
            }
        }
    }
}
