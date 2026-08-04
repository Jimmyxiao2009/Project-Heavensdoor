using System;
using System.Linq;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class MainPage : Page
    {
        private readonly MainViewModel _vm;
        private const string NotificationsSettingKey = "qqr.settings.notifications";
        private string _section = ShellPage.SectionChats;

        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            _vm = new MainViewModel(App.ChatService);
            DataContext = _vm;
            _vm.ContactGroups.CollectionChanged += (_, __) => UpdateContactAlphabetState();
            UpdateContactAlphabetState();
        }

        /// <summary>Invoked from Shell hamburger actions.</summary>
        public void HandleShellAction(string action)
        {
            switch (action)
            {
                case "MarkAllRead":
                    MarkAllRead_Click(this, new RoutedEventArgs());
                    break;
                case "MuteAll":
                    MuteAll_Click(this, new RoutedEventArgs());
                    break;
                case "UnmuteAll":
                    UnmuteAll_Click(this, new RoutedEventArgs());
                    break;
                case "Refresh":
                    _ = SoftRefreshFromShellAsync();
                    break;
            }
        }

        private async System.Threading.Tasks.Task SoftRefreshFromShellAsync()
        {
            try { await _vm.SoftRefreshAsync(force: true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Refresh failed: " + ex); }
        }

        private void ApplySection(string section)
        {
            _section = string.IsNullOrEmpty(section) ? ShellPage.SectionChats : section;
            var contacts = string.Equals(_section, ShellPage.SectionContacts, StringComparison.OrdinalIgnoreCase);
            if (ChatsPanel != null) ChatsPanel.Visibility = contacts ? Visibility.Collapsed : Visibility.Visible;
            if (ContactsPanel != null) ContactsPanel.Visibility = contacts ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateContactAlphabetState()
        {
            if (ContactAlphabetBar == null) return;

            var available = new HashSet<string>(
                _vm.ContactGroups.Select(group => group.Key),
                StringComparer.OrdinalIgnoreCase);

            foreach (var button in ContactAlphabetBar.Children.OfType<Button>())
            {
                var key = button.Tag as string;
                var enabled = !string.IsNullOrEmpty(key) && available.Contains(key);
                button.IsEnabled = enabled;
                button.Opacity = enabled ? 1.0 : 0.28;
            }
        }

        private async void OnGlobalMessageReceived(object sender, ChatMessage msg)
        {
            if (msg == null) return;
            if (msg.Direction == MessageDirection.Outgoing) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!NotificationsEnabled()) return;
                if (NotificationMuteGate.ShouldSuppressNotification(msg.ConversationId)) return;

                // Only when shell is showing chats home
                var root = Window.Current.Content as Frame;
                if (root?.Content is ShellPage shell)
                {
                    // ok — under shell
                }
                else if (root?.CurrentSourcePageType != typeof(ShellPage) && root?.CurrentSourcePageType != typeof(MainPage))
                {
                    return;
                }

                var conv = _vm.Conversations.FirstOrDefault(c => c.Id == msg.ConversationId);
                var preview = MessagePresentation.GetSummary(msg);

                var isGroup = (conv != null && conv.IsGroup)
                    || (!string.IsNullOrEmpty(msg.ConversationId)
                        && msg.ConversationId.StartsWith("g", StringComparison.OrdinalIgnoreCase));

                string title;
                string avatar;
                string body;
                if (isGroup)
                {
                    title = !string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle
                        : (conv != null && !string.IsNullOrEmpty(conv.Title) ? conv.Title : msg.SenderName);
                    avatar = !string.IsNullOrEmpty(msg.ConversationAvatarPath) ? msg.ConversationAvatarPath
                        : (conv != null ? conv.AvatarPath : null);
                    if (string.IsNullOrEmpty(avatar) && !string.IsNullOrEmpty(msg.ConversationId) && msg.ConversationId.Length > 1)
                    {
                        long g;
                        if (long.TryParse(msg.ConversationId.Substring(1), out g) && g > 0)
                            avatar = "https://p.qlogo.cn/gh/" + g + "/" + g + "/100";
                    }
                    body = string.IsNullOrEmpty(msg.SenderName) ? preview : (msg.SenderName + ": " + preview);
                }
                else
                {
                    title = conv != null && !string.IsNullOrEmpty(conv.Title) ? conv.Title
                        : (!string.IsNullOrEmpty(msg.ConversationTitle) ? msg.ConversationTitle : msg.SenderName);
                    avatar = conv != null && !string.IsNullOrEmpty(conv.AvatarPath) ? conv.AvatarPath
                        : (!string.IsNullOrEmpty(msg.ConversationAvatarPath) ? msg.ConversationAvatarPath : msg.SenderAvatarPath);
                    body = preview;
                }

                if (Banner == null) return;
                if (conv != null)
                    Banner.Show(avatar, title, body, () => AppNav.ToRoot(typeof(ConversationPage), conv));
                else
                    Banner.Show(avatar, title, body, null);
            });
        }

        private static bool NotificationsEnabled()
        {
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values[NotificationsSettingKey];
                return !(raw is bool b) || b;
            }
            catch { return true; }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplySection(e.Parameter as string);
            App.ClearRememberedConversation();
            App.ChatService.MessageReceived -= OnGlobalMessageReceived;
            App.ChatService.MessageReceived += OnGlobalMessageReceived;
            _vm.Attach();
            _ = LoadHomeInBackgroundAsync();
        }

        private async System.Threading.Tasks.Task LoadHomeInBackgroundAsync()
        {
            try
            {
                if (!_vm.IsLoaded)
                {
                    await _vm.LoadAsync();
                    return;
                }
                await _vm.SoftRefreshAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Home background load failed: " + ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ResetQuickPanelState();
            base.OnNavigatedFrom(e);
            App.ChatService.MessageReceived -= OnGlobalMessageReceived;
            _vm.Detach();
        }

        private void ConversationList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatConversation conv)
            {
                conv.Unread = 0;
                UnreadBadgeStore.Clear(conv.Id);
                AppNav.ToRoot(typeof(ConversationPage), conv);
            }
        }

        private void SearchBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AppNav.ToRoot(typeof(SearchPage));
        }
    }
}
