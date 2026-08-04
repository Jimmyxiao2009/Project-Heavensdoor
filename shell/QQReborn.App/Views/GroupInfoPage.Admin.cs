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
            if (!(AppServices.Gateway != null))
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

        private async System.Threading.Tasks.Task<IGatewayService> RequireRemoteAsync(string demoMessage)
        {
            var remote = AppServices.Gateway;
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
