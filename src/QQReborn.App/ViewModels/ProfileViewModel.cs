using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>
    /// Backs the "我" (profile) area. Pulls self identity from the shared chat
    /// backend and decorates it with cosmetic extras from a dedicated profile service.
    /// </summary>
    public class ProfileViewModel : ObservableObject
    {
        private readonly IChatService _chat;
        private readonly IProfileService _profile;

        public ProfileViewModel(IChatService chat, IProfileService profile)
        {
            _chat = chat;
            _profile = profile;
        }

        private bool _isLoaded;

        /// <summary>
        /// The live <see cref="SelfProfile"/> loaded from the chat backend. It is an
        /// ObservableObject, so binding to its Status/StatusText/StatusColorHex updates
        /// the UI automatically when the online status changes. Null until LoadAsync runs.
        /// </summary>
        private SelfProfile _self;
        public SelfProfile Self { get => _self; private set => Set(ref _self, value); }

        private string _nickname = "QQ 用户";
        public string Nickname { get => _nickname; private set => Set(ref _nickname, value); }

        private string _signature = "";
        public string Signature { get => _signature; private set => Set(ref _signature, value); }

        private string _avatarPath = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
        public string AvatarPath { get => _avatarPath; private set => Set(ref _avatarPath, value); }

        private string _uinText = "QQ: --";
        public string UinText { get => _uinText; private set => Set(ref _uinText, value); }

        private string _levelText = "Lv.--";
        public string LevelText { get => _levelText; private set => Set(ref _levelText, value); }

        private bool _isVip;
        public bool IsVip { get => _isVip; private set => Set(ref _isVip, value); }

        private string _vipLabel = "会员";
        public string VipLabel { get => _vipLabel; private set => Set(ref _vipLabel, value); }

        public async Task LoadAsync()
        {
            if (_isLoaded) return;
            _isLoaded = true;

            var self = await _chat.GetSelfAsync();
            if (self != null)
            {
                Self = self;
                Nickname = string.IsNullOrEmpty(self.Nickname) ? "QQ 用户" : self.Nickname;
                Signature = self.Signature ?? "";
                if (!string.IsNullOrEmpty(self.AvatarPath)) AvatarPath = self.AvatarPath;
                UinText = "QQ: " + self.Uin;
            }

            var extras = await _profile.GetExtrasAsync();
            if (extras != null)
            {
                LevelText = "Lv." + extras.Level;
                IsVip = extras.IsVip;
                VipLabel = string.IsNullOrEmpty(extras.VipLabel) ? "会员" : extras.VipLabel;
            }
        }

        /// <summary>
        /// Updates the logged-in user's online status. Because <see cref="Self"/> is an
        /// ObservableObject, setting Status raises change notifications for StatusText and
        /// StatusColorHex, so any bound UI (the status dot/label) refreshes automatically.
        /// </summary>
        public void SetStatus(OnlineStatus status)
        {
            if (_self != null) _self.Status = status;
        }
    }
}
