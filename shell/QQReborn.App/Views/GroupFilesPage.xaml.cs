using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    public sealed partial class GroupFilesPage : Page
    {
        public sealed class Args
        {
            public string ConversationId { get; set; }
            public string Title { get; set; }
            public string FolderId { get; set; }
            public string FolderName { get; set; }
        }

        private readonly ObservableCollection<GroupFileEntry> _items = new ObservableCollection<GroupFileEntry>();
        private string _conversationId;
        private string _folderId;
        private bool _selfIsAdmin;

        public GroupFilesPage()
        {
            InitializeComponent();
            FileList.ItemsSource = _items;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as Args;
            if (args == null || string.IsNullOrEmpty(args.ConversationId))
            {
                EmptyText.Text = "无效的群会话";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            _conversationId = args.ConversationId;
            _folderId = args.FolderId;
            TitleText.Text = string.IsNullOrEmpty(args.FolderName)
                ? (string.IsNullOrEmpty(args.Title) ? "群文件" : args.Title + " · 文件")
                : args.FolderName;

            await ResolveAdminAsync();
            await LoadAsync();
        }

        private async System.Threading.Tasks.Task ResolveAdminAsync()
        {
            _selfIsAdmin = false;
            try
            {
                var self = await App.ChatService.GetSelfAsync();
                var selfUin = self != null ? self.Uin : 0;
                if (selfUin <= 0) return;
                var members = await App.ChatService.GetGroupMembersAsync(_conversationId);
                var me = members != null
                    ? members.FirstOrDefault(m => m != null && m.Uin == selfUin)
                    : null;
                _selfIsAdmin = me != null && me.IsAdmin;
            }
            catch { _selfIsAdmin = false; }

            // 新建文件夹仅管理可见
            NewFolderButton.Visibility = _selfIsAdmin ? Visibility.Visible : Visibility.Collapsed;
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _items.Clear();
            EmptyText.Text = "加载中…";
            EmptyText.Visibility = Visibility.Visible;

            var remote = AppServices.Gateway;
            if (remote == null)
            {
                EmptyText.Text = "演示模式不支持群文件";
                return;
            }

            try
            {
                var result = await remote.GetGroupFilesAsync(_conversationId, _folderId);
                foreach (var f in result.Folders) _items.Add(f);
                foreach (var f in result.Files) _items.Add(f);

                if (_items.Count == 0)
                {
                    EmptyText.Text = "暂无文件";
                    EmptyText.Visibility = Visibility.Visible;
                }
                else
                {
                    EmptyText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                EmptyText.Text = "加载失败";
                EmptyText.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine("GroupFiles: " + ex);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!_selfIsAdmin)
            {
                try { await new MessageDialog("仅群主/管理员可新建文件夹。", "提示").ShowAsync(); }
                catch { }
                return;
            }
            var remote = AppServices.Gateway;
            if (remote == null)
            {
                try { await new MessageDialog("演示模式不支持。", "提示").ShowAsync(); }
                catch { }
                return;
            }

            var input = new TextBox { PlaceholderText = "文件夹名称", Margin = new Thickness(0, 12, 0, 0) };
            var dialog = new ContentDialog
            {
                Title = "新建文件夹",
                Content = input,
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
            };
            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;
            var name = (input.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var ok = await remote.CreateGroupFolderAsync(_conversationId, name);
                if (ok) await LoadAsync();
                else await new MessageDialog("创建失败", "提示").ShowAsync();
            }
            catch
            {
                try { await new MessageDialog("创建失败", "提示").ShowAsync(); }
                catch { }
            }
        }

        private async void FileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as GroupFileEntry;
            if (item == null) return;

            if (item.IsFolder)
            {
                Frame.Navigate(typeof(GroupFilesPage), new Args
                {
                    ConversationId = _conversationId,
                    FolderId = item.FolderId,
                    FolderName = item.Name,
                });
                return;
            }

            var remote = AppServices.Gateway;
            if (remote == null) return;

            try
            {
                var url = await remote.GetGroupFileUrlAsync(_conversationId, item.FileId, item.Busid);
                if (string.IsNullOrEmpty(url))
                {
                    await new MessageDialog("无法获取下载链接", "群文件").ShowAsync();
                    return;
                }

                var dialog = new MessageDialog(item.Name + "\n\n" + url, "文件链接");
                var open = new UICommand("打开");
                dialog.Commands.Add(open);
                UICommand del = null;
                if (_selfIsAdmin)
                {
                    del = new UICommand("删除");
                    dialog.Commands.Add(del);
                }
                dialog.Commands.Add(new UICommand("关闭"));
                dialog.DefaultCommandIndex = 0;
                var chosen = await dialog.ShowAsync();
                if (chosen == open)
                {
                    try { await Launcher.LaunchUriAsync(new Uri(url)); }
                    catch { }
                }
                else if (del != null && chosen == del)
                {
                    var ok = await remote.DeleteGroupFileAsync(_conversationId, item.FileId, item.Busid);
                    if (ok)
                    {
                        _items.Remove(item);
                        if (_items.Count == 0)
                        {
                            EmptyText.Text = "暂无文件";
                            EmptyText.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        try { await new MessageDialog("删除失败", "提示").ShowAsync(); }
                        catch { }
                    }
                }
            }
            catch
            {
                try { await new MessageDialog("获取链接失败", "群文件").ShowAsync(); }
                catch { }
            }
        }
    }
}
