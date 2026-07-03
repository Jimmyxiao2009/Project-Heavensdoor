using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Fake video-call screen. The remote side is mocked (no peer/transport), but the LOCAL
    /// camera preview is real via MediaCapture + CaptureElement. Buttons toggle local state.
    /// The MediaCapture is always disposed in OnNavigatedFrom so the camera is never leaked.
    /// </summary>
    public sealed partial class VideoCallPage : Page
    {
        private MediaCapture _mediaCapture;
        private bool _previewing;
        private bool _cameraAvailable;
        private Windows.Devices.Enumeration.Panel _currentPanel = Windows.Devices.Enumeration.Panel.Front;

        private DispatcherTimer _connectTimer;
        private DispatcherTimer _durationTimer;
        private TimeSpan _elapsed;

        private bool _muted;
        private bool _videoOff;

        public VideoCallPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
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

            await InitializeCameraAsync(_currentPanel);
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopTimers();
            await CleanupCameraAsync();
            base.OnNavigatedFrom(e);
        }

        // ---- camera ----

        /// <summary>Find the device id of a camera on the given panel, else any camera, else null.</summary>
        private static async Task<string> FindCameraIdAsync(Windows.Devices.Enumeration.Panel panel)
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices == null || devices.Count == 0) return null;

            foreach (var d in devices)
            {
                if (d.EnclosureLocation != null && d.EnclosureLocation.Panel == panel)
                    return d.Id;
            }
            return devices[0].Id; // fall back to the first available camera
        }

        private async Task InitializeCameraAsync(Windows.Devices.Enumeration.Panel panel)
        {
            try
            {
                var id = await FindCameraIdAsync(panel);

                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo
                };
                if (!string.IsNullOrEmpty(id)) settings.VideoDeviceId = id;

                _mediaCapture = new MediaCapture();
                await _mediaCapture.InitializeAsync(settings);

                PreviewElement.Source = _mediaCapture;
                await _mediaCapture.StartPreviewAsync();

                _previewing = true;
                _cameraAvailable = true;
                _videoOff = false;
                NoCameraPanel.Visibility = Visibility.Collapsed;
                PreviewElement.Visibility = Visibility.Visible;
            }
            catch (UnauthorizedAccessException)
            {
                _cameraAvailable = false;
                ShowNoCamera("摄像头/麦克风被禁用：设置 > 隐私 > 摄像头");
                await CleanupCameraAsync();
            }
            catch (Exception)
            {
                _cameraAvailable = false;
                ShowNoCamera("摄像头不可用");
                await CleanupCameraAsync();
            }
        }

        private void ShowNoCamera(string message)
        {
            NoCameraText.Text = message;
            NoCameraPanel.Visibility = Visibility.Visible;
            PreviewElement.Visibility = Visibility.Collapsed;
        }

        private async Task CleanupCameraAsync()
        {
            if (_mediaCapture == null) { _previewing = false; return; }
            try
            {
                if (_previewing) await _mediaCapture.StopPreviewAsync();
            }
            catch (Exception) { }
            finally
            {
                _previewing = false;
                try { _mediaCapture.Dispose(); } catch (Exception) { }
                _mediaCapture = null;
                PreviewElement.Source = null;
            }
        }

        // ---- mock calling flow ----

        private void StartMockFlow()
        {
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

        // ---- controls ----

        private async void Switch_Click(object sender, RoutedEventArgs e)
        {
            if (!_cameraAvailable) return;

            // Flip the desired panel and re-init against the other camera.
            _currentPanel = _currentPanel == Windows.Devices.Enumeration.Panel.Front
                ? Windows.Devices.Enumeration.Panel.Back
                : Windows.Devices.Enumeration.Panel.Front;

            await CleanupCameraAsync();
            await InitializeCameraAsync(_currentPanel);

            // If video had been turned off, honor that after the switch.
            if (_videoOff && _mediaCapture != null)
            {
                PreviewElement.Visibility = Visibility.Collapsed;
                ShowNoCamera("视频已关闭");
            }
        }

        private void ToggleVideo_Click(object sender, RoutedEventArgs e)
        {
            _videoOff = !_videoOff;
            VideoGlyph.Text = _videoOff ? "\U0001F6AB" : "\U0001F4F7"; // 🚫 / 📷
            VideoLabel.Text = _videoOff ? "开启视频" : "关闭视频";
            VideoButton.Background = _videoOff
                ? (Brush)Application.Current.Resources["MetroAccentBrush"]
                : (Brush)Application.Current.Resources["MetroPanelBrush"];

            if (_videoOff)
            {
                PreviewElement.Visibility = Visibility.Collapsed;
                ShowNoCamera("视频已关闭");
            }
            else if (_cameraAvailable)
            {
                NoCameraPanel.Visibility = Visibility.Collapsed;
                PreviewElement.Visibility = Visibility.Visible;
            }
            else
            {
                // Camera never came up; keep the unavailable message.
                ShowNoCamera("摄像头不可用");
            }
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _muted = !_muted;
            MuteGlyph.Text = _muted ? "\U0001F507" : "\U0001F3A4"; // 🔇 / 🎤
            MuteLabel.Text = _muted ? "已静音" : "静音";
            MuteButton.Background = _muted
                ? (Brush)Application.Current.Resources["MetroAccentBrush"]
                : (Brush)Application.Current.Resources["MetroPanelBrush"];
        }

        private async void HangUp_Click(object sender, RoutedEventArgs e)
        {
            StopTimers();
            await CleanupCameraAsync();
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
