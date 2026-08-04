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
        // ---- group @mention ----

        private async void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_vm.IsGroup) return;
            if (_mentionFlyoutOpen) return;

            var text = InputBox.Text ?? string.Empty;
            // Trigger only when the user just typed a trailing "@" at the caret end.
            if (text.Length == 0 || text[text.Length - 1] != '@') return;
            if (InputBox.SelectionStart != text.Length) return;

            await ShowMentionPickerAsync();
        }

        private bool _mentionFlyoutOpen;

        private async System.Threading.Tasks.Task ShowMentionPickerAsync()
        {
            IReadOnlyList<GroupMember> members;
            try
            {
                members = await _chat.GetGroupMembersAsync(_vm.ConversationId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ShowMentionPicker: " + ex.Message);
                return;
            }
            if (members == null) members = Array.Empty<GroupMember>();

            var menu = new MenuFlyout { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

            // @全体成员
            var allItem = new MenuFlyoutItem { Text = "全体成员" };
            allItem.Click += (s, args) => InsertMention(new GroupMember { Uin = 0, Name = "全体成员", Role = "" });
            menu.Items.Add(allItem);

            // 群主/管理优先（服务端已排序，这里再稳妥排一次）
            var ordered = members
                .OrderBy(m => m != null && m.IsOwner ? 0 : (m != null && m.Role == "管理员" ? 1 : 2))
                .ThenBy(m => m != null ? (m.Name ?? "") : "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var member in ordered)
            {
                if (member == null || member.Uin <= 0) continue;
                var captured = member;
                var label = string.IsNullOrEmpty(captured.Name) ? captured.Uin.ToString() : captured.Name;
                if (!string.IsNullOrEmpty(captured.Role))
                    label = label + "（" + captured.Role + "）";
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (s, args) => InsertMention(captured);
                menu.Items.Add(item);
            }

            if (menu.Items.Count <= 1 && ordered.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "暂无成员数据", IsEnabled = false });
            }

            _mentionFlyoutOpen = true;
            menu.Closed += (s, args) => _mentionFlyoutOpen = false;
            menu.ShowAt(InputBox);
        }

        private void InsertMention(GroupMember member)
        {
            var name = member == null || string.IsNullOrEmpty(member.Name)
                ? (member != null && member.Uin > 0 ? member.Uin.ToString() : "全体成员")
                : member.Name;
            var display = name.StartsWith("@", StringComparison.Ordinal) ? name : ("@" + name);
            var text = _vm.Draft ?? string.Empty;
            // Replace the trailing "@" that opened the picker with "@昵称 ".
            if (text.Length > 0 && text[text.Length - 1] == '@')
                text = text.Substring(0, text.Length - 1);
            _vm.Draft = text + display + " ";
            _vm.PendingMentions.Add(new ViewModels.ConversationViewModel.MentionInfo
            {
                // uin=0 → NapCat at qq=all
                Uin = member != null ? member.Uin : 0,
                Display = display,
            });

            // Put the caret at the end so typing continues naturally.
            InputBox.SelectionStart = InputBox.Text != null ? InputBox.Text.Length : 0;
            InputBox.Focus(FocusState.Programmatic);
        }

        private void GroupInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Conversation != null)
                Frame.Navigate(typeof(GroupInfoPage), _vm.Conversation);
        }

        private void MessageArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // The message list stretches to fill the middle area. A tap on its unused
            // lower area is a deliberate lightweight way back to the conversation list;
            // taps inside a message item are left for that item's own handlers.
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is ListViewItem) return;
                source = VisualTreeHelper.GetParent(source);
            }

            e.Handled = true;
            if (Frame.CanGoBack) Frame.GoBack();
        }

        // ---- chat background picker ----

        private void ChatBgButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate without a parameter so OnNavigatedTo keeps the current chat and
            // simply re-applies "ChatBackground" when we come back from the picker.
            Frame.Navigate(typeof(QQReborn.App.Views.ChatBackgroundPage));
        }

        // ---- load earlier history (fabricated for the mock backend, real for remote --
        // see ConversationViewModel.LoadEarlierAsync) ----

        private async void LoadEarlierStrip_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            // Remember the item that was first on screen so we can keep it in view after
            // LoadEarlierAsync prepends older messages ahead of it -- both the mock batch and
            // the real remote page insert at index 0 and never touch/remove this item, they
            // only shift its index up, so it's always still in Messages afterwards.
            // ScrollIntoView is a best-effort "restore roughly where the user was reading" --
            // it snaps the anchor item to whatever position the ListView chooses (not
            // necessarily pixel-identical to before the insert), but that's the only scroll-
            // position primitive this ListView exposes (no ViewChanged-based offset capture
            // is wired up here), and it's enough to avoid the jarring jump to the very top
            // that a bare Messages.Insert(0, ...) would otherwise cause.
            var anchor = _vm.Messages.Count > 0 ? _vm.Messages[0] : null;
            await _vm.LoadEarlierAsync();
            if (anchor != null) MessageList.ScrollIntoView(anchor);
        }

        private void ClearReplyButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearReplyTarget();
        }

        // ---- 发送位置 ----

        private async void LocationButton_Click(object sender, RoutedEventArgs e)
        {
            string address;
            double latitude;
            double longitude;
            try
            {
                var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
                if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                {
                    await new MessageDialog("系统没有授予定位权限，无法发送真实位置。", "发送位置").ShowAsync();
                    return;
                }
                var geo = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 100 };
                var pos = await geo.GetGeopositionAsync();
                var p = pos.Coordinate.Point.Position;
                latitude = p.Latitude;
                longitude = p.Longitude;
                address = "纬度 " + latitude.ToString("F5") + ", 经度 " + longitude.ToString("F5");
            }
            catch (Exception ex)
            {
                await new MessageDialog("无法获取当前位置：" + ex.Message, "发送位置").ShowAsync();
                return;
            }

            await _vm.SendLocationAsync("我的位置", address, null, latitude, longitude);
            ScrollToBottom();
        }

        // ---- 戳一戳 (double tap an incoming bubble) ----

        private async void Bubble_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!UtilitySettings.DoubleTapNudge) return;
            if (!((sender as FrameworkElement)?.DataContext is ChatMessage m)) return;
            if (!m.IsIncoming) return;
            e.Handled = true;

            var who = string.IsNullOrEmpty(m.SenderName) ? "对方" : m.SenderName;

            // Against a remote backend, actually send the nudge on the wire before showing
            // the local notice -- friend chats target the peer, group chats target whoever
            // sent the bubble that was double-tapped (m.SenderUin is that sender's uin in
            // both cases: for a friend conversation it's always the peer, since only the
            // peer can be the sender of an incoming message; see BotSessionManager.SendNudgeAsync).
            if (_chat is IGatewayService remote)
            {
                bool sent;
                try
                {
                    sent = await remote.SendNudgeAsync(_vm.ConversationId, m.SenderUin);
                }
                catch (Exception)
                {
                    sent = false;
                }
                if (!sent)
                {
                    _vm.AppendSystem("戳一戳发送失败");
                    ScrollToBottom();
                    return;
                }
            }

            _vm.AppendSystem("你戳了戳 " + who);
            ScrollToBottom();
        }

    }
}
