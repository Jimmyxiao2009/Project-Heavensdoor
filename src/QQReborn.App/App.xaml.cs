using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using QQReborn.App.Services;
using QQReborn.App.Views;

namespace QQReborn.App
{
    public sealed partial class App : Application
    {
        /// <summary>
        /// App-wide chat backend. Flip to false to use the in-app mock instead of the
        /// local WebSocket fake server (QQReborn.FakeServer must be running for Remote).
        /// </summary>
        private const bool UseRemoteBackend = false;

        public static IChatService ChatService { get; } =
            UseRemoteBackend ? (IChatService)new RemoteChatService() : new MockChatService();

        /// <summary>
        /// Id of the conversation currently open on screen, or null if none. Used so the
        /// conversation list does not bump an unread badge for messages the user is reading live.
        /// Set by ConversationPage on navigation in, cleared on navigation out.
        /// </summary>
        public static string ActiveConversationId { get; set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            System.Diagnostics.Debug.WriteLine(">>>>> QQREBORN UNHANDLED >>>>>");
            System.Diagnostics.Debug.WriteLine("MESSAGE: " + e.Message);
            System.Diagnostics.Debug.WriteLine("EXCEPTION: " + (ex != null ? ex.ToString() : "(null)"));
            System.Diagnostics.Debug.WriteLine(">>>>> END >>>>>");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame rootFrame))
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }
    }
}
