using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>
    /// Backs the "新朋友" (friend requests) page. Loads pending requests from
    /// the chat backend and accepts them in place (each FriendRequest is an
    /// ObservableObject, so flipping Handled updates the row live).
    /// </summary>
    public class FriendRequestsViewModel : ObservableObject
    {
        private readonly IChatService _chat;

        public FriendRequestsViewModel(IChatService chat)
        {
            _chat = chat;
        }

        public ObservableCollection<FriendRequest> Requests { get; } = new ObservableCollection<FriendRequest>();

        private bool _hasRequests;
        public bool HasRequests { get => _hasRequests; private set => Set(ref _hasRequests, value); }

        private bool _isLoaded;

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            try
            {
                var requests = await _chat.GetFriendRequestsAsync();
                Requests.Clear();
                if (requests != null)
                {
                    foreach (var r in requests) Requests.Add(r);
                }
            }
            catch (Exception)
            {
                // Backend unavailable (e.g. remote timeout): fall back to the empty state
                // instead of letting the exception escape the async-void navigation handler.
                // Allow a later visit to retry.
                Requests.Clear();
                _isLoaded = false;
            }
            HasRequests = Requests.Count > 0;
        }

        public async Task AcceptAsync(FriendRequest request)
        {
            if (request == null || request.Handled) return;
            try
            {
                await _chat.AcceptFriendRequestAsync(request);
            }
            catch (Exception)
            {
                // Accept failed against the backend; leave the row pending for a retry.
            }
            // AcceptFriendRequestAsync already sets request.Handled based on what the
            // backend actually did (mock: true; real server: honestly false, it has no
            // API to accept a friend request) -- don't override that here.
        }

        public async Task RejectAsync(FriendRequest request)
        {
            if (request == null || request.Handled) return;
            try
            {
                await _chat.RejectFriendRequestAsync(request);
            }
            catch (Exception)
            {
                // Leave pending for retry.
            }
        }
    }
}
