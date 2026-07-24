namespace QQReborn.App.Models
{
    /// <summary>Cosmetic, gamified profile extras shown in the "我" area.</summary>
    public class ProfileExtras
    {
        public int Level { get; set; }
        public bool IsVip { get; set; }
        public string VipLabel { get; set; }
    }

    /// <summary>App-level toggles surfaced on the Settings page.</summary>
    public class AppSettings
    {
        public bool DarkMode { get; set; } = true;
        public bool Notifications { get; set; } = true;
        public bool EnterToSend { get; set; } = true;

        /// <summary>0 = 小, 1 = 标准, 2 = 大.</summary>
        public int FontSizeLevel { get; set; } = 1;

        /// <summary>Host of the fake/real server (no scheme/port); "localhost" on desktop,
        /// the PC's LAN IP on a phone. Consumed by RemoteChatService when remote backend is on.</summary>
        public string ServerHost { get; set; } = "localhost";

        /// <summary>Where the real-account sign (T544) requests go: false = Lagrange's
        /// official community sign service (needs a #signer-registered API key), true = a
        /// self-hosted sign server the user points at directly.</summary>
        public bool UseSelfHostedSignServer { get; set; } = false;

        /// <summary>Sign server base URL. Ignored (assumed to be the official Lagrange URL)
        /// when UseSelfHostedSignServer is false; user-provided when true.</summary>
        public string SignServerUrl { get; set; } = "https://sign.lagrangecore.org";

        /// <summary>Bearer token for the sign server. Required for the official service,
        /// optional for a self-hosted one.</summary>
        public string SignToken { get; set; } = "";

        /// <summary>QQ number to log in as.</summary>
        public string SignUin { get; set; } = "";
    }
}
