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
        // ---- long-press menu ----

        private void Bubble_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            ShowMessageMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Bubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // Touch/pen long-press raises both Holding (Started) and, on release, RightTapped
            // for the same gesture -- without this guard the menu popped up twice. Only mouse
            // right-click reaches here; Holding above owns touch/pen.
            if (e.PointerDeviceType != Windows.Devices.Input.PointerDeviceType.Mouse) return;
            ShowMessageMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowMessageMenu(FrameworkElement anchor)
        {
            if (!(anchor?.DataContext is ChatMessage m)) return;
            if (m.IsSystem) return;

            var menu = new MenuFlyout();

            // Always allow copy of a text summary (image/file -> placeholder).
            {
                var copy = new MenuFlyoutItem { Text = "复制" };
                copy.Click += (s, e) =>
                {
                    var text = ConversationViewModel.FormatForCopy(m, UtilitySettings.CopyWithSender);
                    if (string.IsNullOrEmpty(text) && m.IsText) text = m.Text ?? string.Empty;
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText(text ?? string.Empty);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                };
                menu.Items.Add(copy);
            }

            var multi = new MenuFlyoutItem { Text = "多选" };
            multi.Click += (s, e) => EnterMultiSelectMode(m);
            menu.Items.Add(multi);

            // 回复
            var reply = new MenuFlyoutItem { Text = "回复" };
            reply.Click += (s, e) => _vm.ReplyTarget = m;
            menu.Items.Add(reply);

            // 群消息回应
            if (_vm.IsGroup && _chat is IGatewayService)
            {
                var react = new MenuFlyoutSubItem { Text = "回应" };
                foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" })
                {
                    var captured = emoji;
                    var item = new MenuFlyoutItem { Text = captured };
                    item.Click += async (s, e) =>
                    {
                        var remote = _chat as IGatewayService;
                        if (remote == null) return;
                        var adding = !m.Reactions.Contains(captured);
                        try
                        {
                            var ok = await remote.SetGroupReactionAsync(_vm.ConversationId, m.Id, captured, adding);
                            if (ok) m.ToggleReaction(captured);
                            else
                            {
                                _vm.AppendSystem("回应失败");
                                ScrollToBottom();
                            }
                        }
                        catch (Exception ex)
                        {
                            _vm.AppendSystem("回应失败：" + ex.Message);
                            ScrollToBottom();
                        }
                    };
                    react.Items.Add(item);
                }
                menu.Items.Add(react);

                // 设/取消精华：仅群主或管理员
                if (_selfIsGroupAdmin)
                {
                    var essence = new MenuFlyoutItem { Text = "设为精华" };
                    essence.Click += async (s, e) =>
                    {
                        var remote = _chat as IGatewayService;
                        if (remote == null) return;
                        try
                        {
                            var ok = await remote.SetEssenceAsync(m.Id, true);
                            _vm.AppendSystem(ok ? "已设为精华消息" : "设置精华失败");
                            ScrollToBottom();
                        }
                        catch (Exception ex)
                        {
                            _vm.AppendSystem("设置精华失败：" + ex.Message);
                            ScrollToBottom();
                        }
                    };
                    menu.Items.Add(essence);

                    var unEssence = new MenuFlyoutItem { Text = "取消精华" };
                    unEssence.Click += async (s, e) =>
                    {
                        var remote = _chat as IGatewayService;
                        if (remote == null) return;
                        try
                        {
                            var ok = await remote.SetEssenceAsync(m.Id, false);
                            _vm.AppendSystem(ok ? "已取消精华" : "取消精华失败");
                            ScrollToBottom();
                        }
                        catch (Exception ex)
                        {
                            _vm.AppendSystem("取消精华失败：" + ex.Message);
                            ScrollToBottom();
                        }
                    };
                    menu.Items.Add(unEssence);
                }
            }

            // 语音转文字
            if (m.IsVoice && _chat is IGatewayService)
            {
                var ptt = new MenuFlyoutItem { Text = "转文字" };
                ptt.Click += async (s, e) =>
                {
                    var remote = _chat as IGatewayService;
                    if (remote == null) return;
                    try
                    {
                        var text = await remote.FetchPttTextAsync(m.Id);
                        _vm.AppendSystem(string.IsNullOrEmpty(text) ? "转文字失败" : ("语音识别：" + text));
                        ScrollToBottom();
                    }
                    catch (Exception ex)
                    {
                        _vm.AppendSystem("转文字失败：" + ex.Message);
                        ScrollToBottom();
                    }
                };
                menu.Items.Add(ptt);
            }

            // 转发（NapCat 合并转发）
            var forward = new MenuFlyoutItem { Text = "转发" };
            forward.Click += async (s, e) => await ShowForwardPickerAsync(m);
            menu.Items.Add(forward);

            // 截图当前消息区域
            var shot = new MenuFlyoutItem { Text = "截图会话" };
            shot.Click += async (s, e) =>
            {
                MessageList.SelectedItems.Clear();
                await MultiScreenshot_ClickAsync();
            };
            menu.Items.Add(shot);

            // 撤回：所有自己发出的非系统消息
            if (m.IsOutgoing)
            {
                var recall = new MenuFlyoutItem { Text = "撤回" };
                recall.Click += async (s, e) => await _vm.RecallMessageAsync(m);
                menu.Items.Add(recall);
            }

            var del = new MenuFlyoutItem { Text = "删除" };
            del.Click += (s, e) => _vm.DeleteMessage(m);
            menu.Items.Add(del);

            menu.ShowAt(anchor);
        }

        private async Task MultiScreenshot_ClickAsync()
        {
            try
            {
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(MessageArea);
                var pixels = await rtb.GetPixelsAsync();
                if (pixels == null || pixels.Length == 0)
                {
                    _vm.AppendSystem("截图失败");
                    return;
                }
                var folder = KnownFolders.PicturesLibrary;
                var file = await folder.CreateFileAsync(
                    "QQReborn_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                    CreationCollisionOption.GenerateUniqueName);
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)rtb.PixelWidth,
                        (uint)rtb.PixelHeight,
                        DisplayInformation.GetForCurrentView().LogicalDpi,
                        DisplayInformation.GetForCurrentView().LogicalDpi,
                        pixels.ToArray());
                    await encoder.FlushAsync();
                }
                _vm.AppendSystem("截图已保存到图片库");
            }
            catch (Exception ex)
            {
                _vm.AppendSystem("截图失败：" + ex.Message);
            }
        }

        // ---- forward: pick a target conversation ----

        private async System.Threading.Tasks.Task ShowForwardPickerAsync(ChatMessage m)
        {
            System.Collections.Generic.IReadOnlyList<ChatConversation> conversations;
            try
            {
                conversations = await _chat.GetConversationsAsync();
            }
            catch (Exception)
            {
                _vm.AppendSystem("转发失败：服务器未连接");
                ScrollToBottom();
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 360
            };
            foreach (var c in conversations) list.Items.Add(c);
            list.DisplayMemberPath = "Title";

            var dialog = new ContentDialog
            {
                Title = "转发到",
                Content = list,
                PrimaryButtonText = "发送",
                SecondaryButtonText = "取消"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            if (!(list.SelectedItem is ChatConversation target)) return;

            ChatMessage sent;
            try
            {
                sent = await _chat.ForwardMessageAsync(target.Id, m.Id);
            }
            catch (Exception)
            {
                _vm.AppendSystem("转发失败：" + target.Title + " 未收到消息");
                ScrollToBottom();
                return;
            }

            if (target.Id == _vm.ConversationId)
            {
                _vm.AppendForwarded(sent);
                ScrollToBottom();
            }
        }

    }
}
