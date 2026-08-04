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
    public sealed partial class GroupInfoPage
    {
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

            var remote = AppServices.Gateway;
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

            var remote = AppServices.Gateway;
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

    }
}
