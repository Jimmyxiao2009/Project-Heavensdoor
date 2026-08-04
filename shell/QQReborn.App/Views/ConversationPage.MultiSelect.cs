using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
// ImageGalleryArgs / ImageGalleryItem live in Models
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Storage.AccessCache;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ConversationPage
    {
        // ---- multi-select ----

        private void MultiSelectHeader_Click(object sender, RoutedEventArgs e)
        {
            if (_multiSelectMode) ExitMultiSelectMode();
            else EnterMultiSelectMode(null);
        }

        private void EnterMultiSelectMode(ChatMessage preselect)
        {
            _multiSelectMode = true;
            MessageList.SelectionMode = ListViewSelectionMode.Multiple;
            MessageList.IsItemClickEnabled = false;
            MessageList.SelectionChanged -= MessageList_SelectionChanged;
            MessageList.SelectionChanged += MessageList_SelectionChanged;
            if (preselect != null) MessageList.SelectedItems.Add(preselect);
            InputBar.Visibility = Visibility.Collapsed;
            MultiSelectBar.Visibility = Visibility.Visible;
            EmojiPanel.Visibility = Visibility.Collapsed;
            UpdateMultiSelectCount();
        }

        private void ExitMultiSelectMode()
        {
            if (!_multiSelectMode && MultiSelectBar != null && MultiSelectBar.Visibility != Visibility.Visible)
                return;
            _multiSelectMode = false;
            if (MessageList != null)
            {
                MessageList.SelectionChanged -= MessageList_SelectionChanged;
                MessageList.SelectedItems.Clear();
                MessageList.SelectionMode = ListViewSelectionMode.None;
            }
            if (InputBar != null) InputBar.Visibility = Visibility.Visible;
            if (MultiSelectBar != null) MultiSelectBar.Visibility = Visibility.Collapsed;
        }

        private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMultiSelectCount();
        }

        private void UpdateMultiSelectCount()
        {
            if (MultiSelectCountText == null || MessageList == null) return;
            MultiSelectCountText.Text = "已选 " + MessageList.SelectedItems.Count + " 条";
        }

        private List<ChatMessage> GetSelectedMessages()
        {
            return MessageList.SelectedItems.OfType<ChatMessage>()
                .Where(m => m != null && !m.IsSystem)
                .OrderBy(m => m.Time)
                .ToList();
        }

        private void MultiSelectCancel_Click(object sender, RoutedEventArgs e) => ExitMultiSelectMode();

        private void MultiCopy_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMessages();
            if (selected.Count == 0) return;
            var withSender = UtilitySettings.CopyWithSender;
            var sb = new StringBuilder();
            foreach (var m in selected)
            {
                var line = ConversationViewModel.FormatForCopy(m, withSender);
                if (string.IsNullOrEmpty(line)) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(line);
            }
            if (sb.Length == 0) return;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(sb.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            _vm.AppendSystem("已复制 " + selected.Count + " 条消息");
            ExitMultiSelectMode();
        }

        private async void MultiForward_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMessages();
            if (selected.Count == 0) return;
            // Forward sequentially to the same target the user picks once.
            System.Collections.Generic.IReadOnlyList<ChatConversation> conversations;
            try { conversations = await _chat.GetConversationsAsync(); }
            catch
            {
                _vm.AppendSystem("转发失败：服务器未连接");
                return;
            }
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
            foreach (var c in conversations) list.Items.Add(c);
            list.DisplayMemberPath = "Title";
            var dialog = new ContentDialog
            {
                Title = "转发 " + selected.Count + " 条到",
                Content = list,
                PrimaryButtonText = "发送",
                SecondaryButtonText = "取消"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var target = list.SelectedItem as ChatConversation;
            if (target == null) return;
            try
            {
                var sent = await _chat.ForwardMessagesAsync(target.Id,
                    selected.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList());
                if (target.Id == _vm.ConversationId && sent != null)
                    _vm.AppendForwarded(sent);
                _vm.AppendSystem(sent != null ? ("已合并转发 " + selected.Count + " 条") : "转发失败");
            }
            catch { _vm.AppendSystem("转发失败"); }
            ScrollToBottom();
            ExitMultiSelectMode();
        }

        private void MultiDelete_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMessages();
            foreach (var m in selected) _vm.DeleteMessage(m);
            ExitMultiSelectMode();
        }

        private async void MultiScreenshot_Click(object sender, RoutedEventArgs e)
        {
            await MultiScreenshot_ClickAsync();
            ExitMultiSelectMode();
        }

    }
}
