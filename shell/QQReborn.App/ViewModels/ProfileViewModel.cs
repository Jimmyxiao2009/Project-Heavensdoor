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

            try
            {
                var self = await _chat.GetSelfAsync();
                if (self != null)
                {
                    Self = self;
                    Nickname = string.IsNullOrEmpty(self.Nickname) ? "QQ 用户" : self.Nickname;
                    Signature = self.Signature ?? "";
                    if (!string.IsNullOrEmpty(self.AvatarPath)) AvatarPath = self.AvatarPath;
                    UinText = "QQ: " + self.Uin;
                }

                var remote = _chat as IGatewayService;
                if (remote != null)
                {
                    // Real backend: there is no VIP concept in the QQ protocol library
                    // (not exposed by the gateway), so the badge is a cosmetic field --
                    // don't fabricate it. Level comes from GetUserProfileAsync; if that
                    // fails or the self uin isn't known yet, fall back to "Lv.--" rather
                    // than showing a made-up number.
                    IsVip = false;
                    LevelText = "Lv.--";
                    if (self != null)
                    {
                        try
                        {
                            var profile = await remote.GetUserProfileAsync(self.Uin);
                            if (profile != null && profile.Level > 0)
                            {
                                LevelText = "Lv." + profile.Level;
                            }
                        }
                        catch (System.Exception)
                        {
                            // Level lookup failed independently of the self-profile load --
                            // keep the "Lv.--" fallback set above instead of crashing.
                        }
                    }
                }
                else
                {
                    var extras = await _profile.GetExtrasAsync();
                    if (extras != null)
                    {
                        LevelText = "Lv." + extras.Level;
                        IsVip = extras.IsVip;
                        VipLabel = string.IsNullOrEmpty(extras.VipLabel) ? "会员" : extras.VipLabel;
                    }
                }
            }
            catch (System.Exception)
            {
                // Remote backend unreachable (server not running / wrong host). The caller
                // is ProfileView's async void Loaded handler -- letting this escape would
                // crash the whole app the moment the user opens the "我" tab. Keep whatever
                // defaults/mock values are already set (see field initializers above) and
                // let the user retry by revisiting the tab or fixing the server address.
                _isLoaded = false;
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

        /// <summary>
        /// Cosmetic, local-only update of the displayed avatar right after a successful
        /// SetAvatarAsync upload -- the server is the source of truth and may take a moment
        /// to actually start serving the new image, so this just avoids the header looking
        /// stale in the meantime. localFilePath is the picked file's own path (a plain
        /// filesystem path, not a URI), so it needs the "file:///" scheme for ImageBrush/
        /// BitmapImage to load it directly.
        /// </summary>
        public void SetLocalAvatarPreview(string localFilePath)
        {
            if (string.IsNullOrEmpty(localFilePath)) return;
            AvatarPath = "file:///" + localFilePath.Replace("\\", "/");
            if (_self != null) _self.AvatarPath = AvatarPath;
        }

        public void ApplyLocalNickname(string nickname)
        {
            if (string.IsNullOrEmpty(nickname)) return;
            Nickname = nickname;
            if (_self != null) _self.Nickname = nickname;
        }

        public void ApplyLocalSignature(string signature)
        {
            Signature = signature ?? "";
            if (_self != null) _self.Signature = Signature;
        }
    }
}
