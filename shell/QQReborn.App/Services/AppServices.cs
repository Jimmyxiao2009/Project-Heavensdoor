namespace QQReborn.App
{
    /// <summary>
    /// Composition root for the shell.
    /// <list type="bullet">
    /// <item><see cref="Chat"/> — always available (remote or mock)</item>
    /// <item><see cref="Gateway"/> — RealServer only; null under mock</item>
    /// <item><see cref="Profile"/> / <see cref="Moments"/> / <see cref="Search"/></item>
    /// </list>
    /// Prefer these over scattering <c>new</c> / concrete casts in pages.
    /// </summary>
    public static class AppServices
    {
        /// <summary>true = RealServer gateway; false = in-app mock (no PC required).</summary>
        public const bool UseRemoteBackend = true;

        private static Services.IChatService _chat;
        private static Services.IProfileService _profile;
        private static Services.IMomentsService _moments;
        private static Services.ISearchService _search;

        public static Services.IChatService Chat
        {
            get
            {
                if (_chat == null)
                {
                    _chat = UseRemoteBackend
                        ? (Services.IChatService)new Services.RemoteChatService()
                        : new Services.MockChatService();
                }
                return _chat;
            }
        }

        /// <summary>
        /// Extended NapCat/RealServer API. Null when using mock chat.
        /// Prefer this over <c>as RemoteChatService</c>.
        /// </summary>
        public static Services.IGatewayService Gateway => Chat as Services.IGatewayService;

        public static Services.IProfileService Profile =>
            _profile ?? (_profile = new Services.MockProfileService());

        public static Services.IMomentsService Moments
        {
            get
            {
                if (_moments != null) return _moments;
                var gw = Gateway;
                _moments = gw != null
                    ? (Services.IMomentsService)new Services.RemoteMomentsService(gw)
                    : new Services.MockMomentsService();
                return _moments;
            }
        }

        public static Services.ISearchService Search =>
            _search ?? (_search = new Services.MockSearchService(Chat));
    }
}
