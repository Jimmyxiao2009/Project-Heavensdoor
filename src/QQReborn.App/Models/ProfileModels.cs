namespace QQReborn.App.Models
{
    /// <summary>Cosmetic, gamified profile extras shown in the "我" area.</summary>
    public class ProfileExtras
    {
        public int Level { get; set; }
        public bool IsVip { get; set; }
        public string VipLabel { get; set; }
        public int FavoriteCount { get; set; }
        public int AlbumCount { get; set; }
        public int FileCount { get; set; }
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
    }
}
