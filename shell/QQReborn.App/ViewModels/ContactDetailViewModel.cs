using System.Text;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;

namespace QQReborn.App.ViewModels
{
    /// <summary>
    /// Read-only wrapper over a <see cref="Contact"/> that exposes the
    /// display strings the ContactDetailPage binds to. The underlying
    /// Contact is a plain model, so this VM just precomputes the labels.
    /// In remote mode the Contact's Gender/Age/Location/Signature start out
    /// empty (the getContacts payload doesn't carry them) -- ApplyProfile
    /// lets the page backfill them once GetUserProfileAsync comes back, then
    /// this VM raises change notifications so the bound TextBlocks refresh.
    /// </summary>
    public class ContactDetailViewModel : ObservableObject
    {
        public Contact Contact { get; }

        public ContactDetailViewModel(Contact contact)
        {
            Contact = contact;
        }

        public string AvatarPath =>
            string.IsNullOrEmpty(Contact?.AvatarPath)
                ? "ms-appx:///Assets/Avatars/DefaultUserAvatar.png"
                : Contact.AvatarPath;

        public string DisplayName => Contact?.DisplayName ?? "";

        public string UinText => "QQ: " + (Contact?.Uin ?? 0);

        public bool Online => Contact != null && Contact.Online;

        public string StatusText =>
            !string.IsNullOrEmpty(_extraHint) ? _extraHint : (Online ? "在线" : "离线");

        private string _extraHint;
        public void ApplyExtraHint(string hint)
        {
            _extraHint = hint ?? "";
            RaisePropertyChanged(nameof(StatusText));
        }

        public string SignatureText =>
            string.IsNullOrEmpty(Contact?.Signature) ? "这个人很懒，什么都没留下" : Contact.Signature;

        /// <summary>"性别 男 · 28岁 · 上海" — segments are skipped when empty.</summary>
        public string DetailText
        {
            get
            {
                if (Contact == null) return "";
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(Contact.Gender)) Append(sb, "性别 " + Contact.Gender);
                if (Contact.Age > 0) Append(sb, Contact.Age + "岁");
                if (!string.IsNullOrEmpty(Contact.Location)) Append(sb, Contact.Location);
                return sb.Length == 0 ? "暂无更多资料" : sb.ToString();
            }
        }

        /// <summary>
        /// Backfills Gender/Age/Location from a freshly-fetched UserProfile and
        /// refreshes the bound labels. Signature is only overwritten when the
        /// contact didn't already have one, so a locally-cached signature (e.g.
        /// from GetContactsAsync) isn't clobbered by a blank server value.
        /// </summary>
        public void ApplyProfile(UserProfile profile)
        {
            if (Contact == null || profile == null) return;

            if (profile.Gender == "male") Contact.Gender = "男";
            else if (profile.Gender == "female") Contact.Gender = "女";

            if (profile.Age > 0) Contact.Age = profile.Age;

            var location = JoinLocation(profile.Country, profile.City);
            if (!string.IsNullOrEmpty(location)) Contact.Location = location;

            if (string.IsNullOrEmpty(Contact.Signature) && !string.IsNullOrEmpty(profile.Signature))
                Contact.Signature = profile.Signature;

            RaisePropertyChanged(nameof(DetailText));
            RaisePropertyChanged(nameof(SignatureText));
        }

        private static string JoinLocation(string country, string city)
        {
            if (string.IsNullOrEmpty(country)) return city ?? "";
            if (string.IsNullOrEmpty(city)) return country;
            return country == city ? country : country + " " + city;
        }

        private static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(part);
        }
    }
}
