using System;
using System.Linq;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using QQReborn.App.Models;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    public sealed partial class MainPage
    {
        private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Contact contact)
                AppNav.ToRoot(typeof(ContactDetailPage), contact);
        }

        private void ContactAlphabet_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(key)) return;

            var group = _vm.ContactGroups.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            var firstContact = group?.FirstOrDefault();
            if (firstContact != null)
                ContactList.ScrollIntoView(firstContact);
        }

        /// <summary>Legacy no-op — quick panel moved into Shell hamburger menu.</summary>
        private void ResetQuickPanelState() { }

        private async void MuteAll_Click(object sender, RoutedEventArgs e)
            => await ApplyMuteAllAsync(true);

        private async void UnmuteAll_Click(object sender, RoutedEventArgs e)
            => await ApplyMuteAllAsync(false);

        private async System.Threading.Tasks.Task ApplyMuteAllAsync(bool muted)
        {
            NotificationMuteGate.SetMuteAll(muted);

            try
            {
                if (!_vm.IsLoaded) await _vm.LoadAsync();
                await _vm.SoftRefreshAsync();
            }
            catch { }

            var targets = _vm.Conversations.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && !IsSpecial(c)).ToList();
            if (AppServices.Gateway is IGatewayService remote)
            {
                try
                {
                    var fresh = await remote.GetConversationsAsync();
                    if (fresh != null)
                    {
                        var seen = new HashSet<string>(targets.Select(t => t.Id));
                        foreach (var c in fresh)
                        {
                            if (c == null || string.IsNullOrEmpty(c.Id) || IsSpecial(c)) continue;
                            if (seen.Contains(c.Id)) continue;
                            targets.Add(c);
                            seen.Add(c.Id);
                        }
                    }
                }
                catch { }
            }

            foreach (var c in targets)
                await ConversationNotificationSettings.TrySetMutedAsync(App.ChatService, c, muted);
        }

        private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
        {
            var remote = AppServices.Gateway;
            if (remote != null)
            {
                try { await remote.MarkAllAsReadAsync(); }
                catch { }
            }

            var pending = new List<System.Threading.Tasks.Task>();
            foreach (var c in _vm.Conversations)
            {
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                c.Unread = 0;
                UnreadBadgeStore.Clear(c.Id);
                if (remote != null)
                    pending.Add(MarkReadSafeAsync(remote, c.Id));
            }

            if (pending.Count > 0)
                await System.Threading.Tasks.Task.WhenAll(pending);
        }

        private static async System.Threading.Tasks.Task MarkReadSafeAsync(IGatewayService remote, string conversationId)
        {
            try { await remote.MarkConversationReadAsync(conversationId, System.DateTimeOffset.UtcNow.ToString("o")); }
            catch { }
        }

        private static bool IsSpecial(ChatConversation c)
            => c != null && NotificationMuteGate.IsSpecial(c.Id);
    }
}
