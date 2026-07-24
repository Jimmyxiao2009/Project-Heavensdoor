using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ContactDetailPage : Page
    {
        private Contact _contact;
        private ContactDetailViewModel _vm;

        public ContactDetailPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is Contact contact)
            {
                _contact = contact;
                _vm = new ContactDetailViewModel(contact);
                DataContext = _vm;

                // Mock data already carries Gender/Age/Location, so there's nothing to
                // backfill there -- only remote mode needs the extra round-trip (getContacts
                // doesn't return those fields).
                if (App.ChatService is RemoteChatService remote)
                {
                    try
                    {
                        var profile = await remote.GetUserProfileAsync(contact.Uin);
                        _vm.ApplyProfile(profile);
                    }
                    catch (Exception)
                    {
                        // Leave the detail block showing "暂无更多资料" -- honest empty state,
                        // no crash on a flaky/unreachable backend.
                    }
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;
            var conv = new ChatConversation
            {
                // Must match the "f{uin}" scheme RemoteChatService/RealServer actually route
                // sends by -- this used to be an ad-hoc "contact-{uin}" placeholder that only
                // ever worked against MockChatService (which doesn't care about id format).
                Id = "f" + _contact.Uin,
                Kind = ConversationKind.Friend,
                Title = _contact.DisplayName,
                AvatarPath = _contact.AvatarPath
            };
            Frame.Navigate(typeof(ConversationPage), conv);
        }

    }
}
