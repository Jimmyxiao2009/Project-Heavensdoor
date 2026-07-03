using System.Text;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;

namespace QQReborn.App.ViewModels
{
    /// <summary>
    /// Read-only wrapper over a <see cref="Contact"/> that exposes the
    /// display strings the ContactDetailPage binds to. The underlying
    /// Contact is a plain model, so this VM just precomputes the labels.
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

        public string StatusText => Online ? "在线" : "离线";

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

        private static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(part);
        }
    }
}
