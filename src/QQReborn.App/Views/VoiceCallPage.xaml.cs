using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Fake voice-call screen. There is no real peer/media transport: this just plays a
    /// convincing "正在呼叫… -> connected -> mm:ss timer" flow and toggles local UI state.
    /// </summary>
    public sealed partial class VoiceCallPage : Page
    {
        private DispatcherTimer _connectTimer;   // one-shot: ringing -> connected
        private DispatcherTimer _durationTimer;  // 1s tick: counts call duration
        private TimeSpan _elapsed;

        private bool _muted;
        private bool _speakerOn;

        public VoiceCallPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var args = e.Parameter as CallArgs;
            if (args != null)
            {
                PeerNameText.Text = string.IsNullOrEmpty(args.PeerName) ? "对方" : args.PeerName;
                if (!string.IsNullOrEmpty(args.PeerAvatar))
                {
                    try { PeerAvatarBrush.ImageSource = new BitmapImage(new Uri(args.PeerAvatar)); }
                    catch (Exception) { }
                }
            }

            StatusText.Text = "正在呼叫…";
            StartMockFlow();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopTimers();
            base.OnNavigatedFrom(e);
        }

        // ---- mock calling flow ----

        private void StartMockFlow()
        {
            // After ~2s of "ringing", switch to connected and start counting up.
            _connectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _connectTimer.Tick += (s, e) =>
            {
                _connectTimer.Stop();
                _connectTimer = null;

                _elapsed = TimeSpan.Zero;
                StatusText.Text = "00:00";

                _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _durationTimer.Tick += DurationTimer_Tick;
                _durationTimer.Start();
            };
            _connectTimer.Start();
        }

        private void DurationTimer_Tick(object sender, object e)
        {
            _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
            StatusText.Text = string.Format("{0:00}:{1:00}",
                (int)_elapsed.TotalMinutes, _elapsed.Seconds);
        }

        private void StopTimers()
        {
            if (_connectTimer != null) { _connectTimer.Stop(); _connectTimer = null; }
            if (_durationTimer != null)
            {
                _durationTimer.Tick -= DurationTimer_Tick;
                _durationTimer.Stop();
                _durationTimer = null;
            }
        }

        // ---- local-only controls ----

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _muted = !_muted;
            MuteGlyph.Text = _muted ? "\U0001F507" : "\U0001F3A4"; // 🔇 / 🎤
            MuteLabel.Text = _muted ? "已静音" : "静音";
            MuteButton.Background = _muted
                ? (Brush)Application.Current.Resources["MetroAccentBrush"]
                : (Brush)Application.Current.Resources["MetroPanelBrush"];
        }

        private void Speaker_Click(object sender, RoutedEventArgs e)
        {
            _speakerOn = !_speakerOn;
            SpeakerGlyph.Text = _speakerOn ? "\U0001F50A" : "\U0001F509"; // 🔊 / 🔉
            SpeakerLabel.Text = _speakerOn ? "免提中" : "免提";
            SpeakerButton.Background = _speakerOn
                ? (Brush)Application.Current.Resources["MetroAccentBrush"]
                : (Brush)Application.Current.Resources["MetroPanelBrush"];
        }

        private void HangUp_Click(object sender, RoutedEventArgs e)
        {
            StopTimers();
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
