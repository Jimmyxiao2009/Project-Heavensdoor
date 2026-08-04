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

        /// <summary>界面大小：0 = 小 (≈82%), 1 = 标准 (100%), 2 = 大 (≈112%).
        /// Applied app-wide via <c>UiScaleService</c>.</summary>
        public int FontSizeLevel { get; set; } = 1;

        /// <summary>Host of the fake/real server (no scheme/port); "localhost" on desktop,
        /// the PC's LAN IP on a phone, or Frp access host (OpenFrp/Sakura/etc.) when outdoors.</summary>
        public string ServerHost { get; set; } = "localhost";

        /// <summary>Wire port (default 8765). Set to Frp remote port when it is not 8765.</summary>
        public int ServerPort { get; set; } = 8765;

        public string AccessPassword { get; set; } = "";

        /// <summary>Where the real-account sign (T544) requests go: false = Lagrange's
        /// official community sign service (needs a #signer-registered API key), true = a
        /// self-hosted sign server the user points at directly. Unused for NapCat local gateway.</summary>
        public bool UseSelfHostedSignServer { get; set; } = false;

        /// <summary>Sign server base URL. Ignored (assumed to be the official Lagrange URL)
        /// when UseSelfHostedSignServer is false; user-provided when true.</summary>
        public string SignServerUrl { get; set; } = "https://sign.lagrangecore.org";

        /// <summary>Bearer token for the sign server. Required for the official service,
        /// optional for a self-hosted one.</summary>
        public string SignToken { get; set; } = "";

        /// <summary>QQ number to log in as.</summary>
        public string SignUin { get; set; } = "";

        // ---- 实用功能 (LocalSettings; defaults favour QQ-like power user) ----

        /// <summary>Keep peer-recalled messages visible locally (防撤回).</summary>
        public bool AntiRecall { get; set; } = true;

        /// <summary>Show a system tip when a message is recalled (even with anti-recall).</summary>
        public bool ShowRevokeNotice { get; set; } = true;

        /// <summary>Double-tap an incoming bubble to poke/nudge.</summary>
        public bool DoubleTapNudge { get; set; } = true;

        /// <summary>Confirm dialog before sending text/image.</summary>
        public bool ConfirmBeforeSend { get; set; } = false;

        /// <summary>When multi-copying, prefix each line with sender name.</summary>
        public bool CopyWithSender { get; set; } = true;

        /// <summary>Vibrate on new message when notifications are on (phone).</summary>
        public bool VibrateOnMessage { get; set; } = false;
    }
}
