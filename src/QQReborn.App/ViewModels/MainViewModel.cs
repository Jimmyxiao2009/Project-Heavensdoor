using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly IChatService _chat;

        public MainViewModel(IChatService chat)
        {
            _chat = chat;
        }

        public ObservableCollection<ChatConversation> Conversations { get; } = new ObservableCollection<ChatConversation>();

        public ObservableCollection<Contact> Contacts { get; } = new ObservableCollection<Contact>();

        private SelfProfile _self;
        public SelfProfile Self { get => _self; private set => Set(ref _self, value); }

        private bool _isLoaded;

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            Self = await _chat.GetSelfAsync();

            var convs = await _chat.GetConversationsAsync();
            Conversations.Clear();
            foreach (var c in convs) Conversations.Add(c);
            ResortConversations();

            var contacts = await _chat.GetContactsAsync();
            Contacts.Clear();
            foreach (var c in contacts) Contacts.Add(c);

            _chat.MessageReceived += OnMessageReceived;
        }

        private void OnMessageReceived(object sender, ChatMessage msg)
        {
            // Bump the conversation's preview/time/unread; the bound properties raise change notifications.
            foreach (var c in Conversations)
            {
                if (c.Id == msg.ConversationId)
                {
                    c.Preview = msg.Text;
                    c.LastTime = msg.Time;
                    // Don't accrue an unread badge for the conversation the user is
                    // currently reading; those messages are seen live on screen.
                    if (msg.ConversationId != App.ActiveConversationId)
                        c.Unread = c.Unread + 1;
                    ResortConversations();
                    break;
                }
            }
        }

        /// <summary>Sort conversations pinned-first, then by most-recent activity. Re-orders the bound collection in place.</summary>
        public void ResortConversations()
        {
            var ordered = Conversations
                .OrderByDescending(c => c.IsPinned)
                .ThenByDescending(c => c.LastTime)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                int current = Conversations.IndexOf(ordered[i]);
                if (current != i) Conversations.Move(current, i);
            }
        }
    }
}
