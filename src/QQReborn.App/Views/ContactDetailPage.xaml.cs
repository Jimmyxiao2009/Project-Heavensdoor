using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class ContactDetailPage : Page
    {
        private Contact _contact;

        public ContactDetailPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is Contact contact)
            {
                _contact = contact;
                DataContext = new ContactDetailViewModel(contact);
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
                Id = "contact-" + _contact.Uin,
                Kind = ConversationKind.Friend,
                Title = _contact.DisplayName,
                AvatarPath = _contact.AvatarPath
            };
            Frame.Navigate(typeof(ConversationPage), conv);
        }

        private void CallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;

            var menu = new MenuFlyout();

            var voice = new MenuFlyoutItem { Text = "语音通话" };
            voice.Click += (s, args) => Frame.Navigate(typeof(VoiceCallPage),
                new CallArgs { PeerName = _contact.DisplayName, PeerAvatar = _contact.AvatarPath, IsVideo = false });
            menu.Items.Add(voice);

            var video = new MenuFlyoutItem { Text = "视频通话" };
            video.Click += (s, args) => Frame.Navigate(typeof(VideoCallPage),
                new CallArgs { PeerName = _contact.DisplayName, PeerAvatar = _contact.AvatarPath, IsVideo = true });
            menu.Items.Add(video);

            menu.ShowAt(sender as FrameworkElement);
        }
    }
}
