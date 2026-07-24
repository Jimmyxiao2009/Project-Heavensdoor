using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;
using QQReborn.App.Models;
using QQReborn.App.Services;

namespace QQReborn.App.Views
{
    /// <summary>
    /// Full-screen image viewer: fit-center, free pan, pinch/wheel zoom, save,
    /// and optional gallery swipe (prev/next) across conversation images.
    /// Parameter: string path/URL, or <see cref="ImageGalleryArgs"/>.
    /// </summary>
    public sealed partial class ImageViewerPage : Page
    {
        private readonly List<ImageGalleryItem> _items = new List<ImageGalleryItem>();
        private int _index;
        private string _sourcePath;

        private double _imgW;
        private double _imgH;
        private double _scale = 1;
        private double _minScale = 1;
        private const double MaxScale = 8;
        private double _tx;
        private double _ty;
        private bool _ready;
        private bool _chromeVisible = true;
        private bool _loading;
        private double _panAccumX; // horizontal pan while fit → gallery flip

        private DispatcherTimer _toastTimer;
        private DispatcherTimer _singleTapTimer;
        private IRandomAccessStream _cachedStream;

        public ImageViewerPage()
        {
            InitializeComponent();
            SizeChanged += (_, __) =>
            {
                if (_ready) FitToScreen(keepRelative: true);
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _items.Clear();
            _index = 0;

            if (e.Parameter is ImageGalleryArgs gallery && gallery.Items != null && gallery.Items.Count > 0)
            {
                _items.AddRange(gallery.Items);
                _index = Math.Max(0, Math.Min(gallery.Index, _items.Count - 1));
            }
            else if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
            {
                _items.Add(new ImageGalleryItem { Path = path });
                _index = 0;
            }
            else
            {
                ShowStatus("无法打开图片");
                return;
            }

            UpdateNavChrome();
            await ShowCurrentAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Viewer.Source = null;
            DisposeStream();
            _singleTapTimer?.Stop();
            _toastTimer?.Stop();
            base.OnNavigatedFrom(e);
        }

        private void DisposeStream()
        {
            if (_cachedStream != null)
            {
                _cachedStream.Dispose();
                _cachedStream = null;
            }
        }

        private void UpdateNavChrome()
        {
            var multi = _items.Count > 1;
            PrevButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            IndexLabel.Text = multi ? (_index + 1) + " / " + _items.Count : "";
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _items.Count - 1;
        }

        private async Task ShowCurrentAsync()
        {
            if (_loading) return;
            _loading = true;
            _ready = false;
            SaveButton.IsEnabled = false;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "加载中…";
            Viewer.Source = null;
            DisposeStream();
            UpdateNavChrome();

            try
            {
                var item = _items[_index];
                var path = item.Path;

                // Resolve empty CDN via RealServer getMediaUrl (same as bubble tap).
                if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(item.MessageId)
                    && App.ChatService is RemoteChatService remote)
                {
                    try { path = await remote.GetMediaUrlAsync(item.MessageId); }
                    catch { path = null; }
                    if (!string.IsNullOrEmpty(path))
                        item.Path = path;
                }

                if (string.IsNullOrEmpty(path))
                {
                    ShowStatus("图片加载失败");
                    return;
                }

                _sourcePath = path;
                await LoadImageAsync(path);
                StatusText.Visibility = Visibility.Collapsed;
                SaveButton.IsEnabled = true;
                FitToScreen(keepRelative: false);
                _ready = true;
                _panAccumX = 0;
            }
            catch (Exception)
            {
                ShowStatus("图片加载失败");
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task LoadImageAsync(string path)
        {
            IRandomAccessStream stream;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using (var http = new HttpClient())
                {
                    var buffer = await http.GetBufferAsync(new Uri(path));
                    stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(buffer);
                    stream.Seek(0);
                }
            }
            else if (path.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase))
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(path));
                stream = await file.OpenReadAsync();
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                stream = await file.OpenReadAsync();
            }

            _cachedStream = stream;

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream.CloneStream());

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream.CloneStream());
            _imgW = decoder.PixelWidth;
            _imgH = decoder.PixelHeight;

            Viewer.Width = _imgW;
            Viewer.Height = _imgH;
            Viewer.Source = bmp;
        }

        private async Task GoAsync(int delta)
        {
            var next = _index + delta;
            if (next < 0 || next >= _items.Count || _loading) return;
            _index = next;
            await ShowCurrentAsync();
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e) => await GoAsync(-1);
        private async void NextButton_Click(object sender, RoutedEventArgs e) => await GoAsync(1);

        // ---- transform ----

        private void ApplyTransform()
        {
            Xf.ScaleX = _scale;
            Xf.ScaleY = _scale;
            Xf.TranslateX = _tx;
            Xf.TranslateY = _ty;
            UpdateZoomLabel();
        }

        private void UpdateZoomLabel()
        {
            if (_imgW <= 0) { ZoomLabel.Text = ""; return; }
            if (Math.Abs(_scale - _minScale) < 0.02)
                ZoomLabel.Text = "适应";
            else if (Math.Abs(_scale - 1.0) < 0.02)
                ZoomLabel.Text = "1:1";
            else
                ZoomLabel.Text = (int)Math.Round(_scale * 100) + "%";
        }

        private void FitToScreen(bool keepRelative)
        {
            var vw = Root.ActualWidth;
            var vh = Root.ActualHeight;
            if (_imgW <= 0 || _imgH <= 0 || vw <= 0 || vh <= 0) return;

            const double chrome = 104;
            var usableH = Math.Max(80, vh - chrome);
            _minScale = Math.Min(vw / _imgW, usableH / _imgH);

            if (!keepRelative)
                _scale = _minScale;
            else
                _scale = Math.Max(_minScale * 0.5, Math.Min(MaxScale, _scale));

            CenterOrClamp();
            ApplyTransform();
        }

        private void SetScaleAt(double newScale, Point pivotInRoot)
        {
            newScale = Math.Max(_minScale * 0.5, Math.Min(MaxScale, newScale));
            if (Math.Abs(newScale - _scale) < 0.0001)
            {
                CenterOrClamp();
                ApplyTransform();
                return;
            }

            var imgX = (pivotInRoot.X - _tx) / _scale;
            var imgY = (pivotInRoot.Y - _ty) / _scale;
            _scale = newScale;
            _tx = pivotInRoot.X - imgX * _scale;
            _ty = pivotInRoot.Y - imgY * _scale;
            CenterOrClamp();
            ApplyTransform();
        }

        private void CenterOrClamp()
        {
            var vw = Root.ActualWidth;
            var vh = Root.ActualHeight;
            var w = _imgW * _scale;
            var h = _imgH * _scale;

            if (w <= vw) _tx = (vw - w) / 2;
            else _tx = Math.Min(0, Math.Max(vw - w, _tx));

            if (h <= vh) _ty = (vh - h) / 2;
            else _ty = Math.Min(0, Math.Max(vh - h, _ty));
        }

        private bool IsFitScale => _scale <= _minScale * 1.08;

        // ---- gestures ----

        private void Viewer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (!_ready) return;

            if (Math.Abs(e.Delta.Scale - 1.0) > 0.0001)
            {
                var local = e.Position;
                var pivotRoot = new Point(local.X * _scale + _tx, local.Y * _scale + _ty);
                SetScaleAt(_scale * e.Delta.Scale, pivotRoot);
                _panAccumX = 0;
            }

            _tx += e.Delta.Translation.X;
            _ty += e.Delta.Translation.Y;

            // At fit scale, accumulate horizontal swipe for gallery flip (don't fight clamp centering).
            if (IsFitScale && _items.Count > 1)
            {
                _panAccumX += e.Delta.Translation.X;
                CenterOrClamp();
                // Rubber-band visual: slight offset while swiping.
                var rubber = Math.Max(-80, Math.Min(80, _panAccumX * 0.35));
                Xf.ScaleX = _scale;
                Xf.ScaleY = _scale;
                Xf.TranslateX = _tx + rubber;
                Xf.TranslateY = _ty;
                UpdateZoomLabel();
            }
            else
            {
                _panAccumX = 0;
                CenterOrClamp();
                ApplyTransform();
            }
            e.Handled = true;
        }

        private async void Viewer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (!_ready) return;
            const double threshold = 90;
            if (IsFitScale && _items.Count > 1 && Math.Abs(_panAccumX) > threshold)
            {
                var delta = _panAccumX < 0 ? 1 : -1; // swipe left → next
                _panAccumX = 0;
                await GoAsync(delta);
                return;
            }
            _panAccumX = 0;
            CenterOrClamp();
            ApplyTransform();
        }

        private void Viewer_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingRoutedEventArgs e)
        {
            // When gallery-swiping at fit, kill inertia so Completed fires with our accum.
            if (IsFitScale && _items.Count > 1 && Math.Abs(_panAccumX) > 40)
            {
                e.TranslationBehavior.DesiredDeceleration = double.MaxValue;
            }
            else
            {
                e.TranslationBehavior.DesiredDeceleration = 0.001;
            }
        }

        private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!_ready) return;
            var pt = e.GetCurrentPoint(Root);
            var factor = pt.Properties.MouseWheelDelta > 0 ? 1.12 : (1.0 / 1.12);
            SetScaleAt(_scale * factor, pt.Position);
            e.Handled = true;
        }

        private void Viewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!_ready) return;
            e.Handled = true;
            CancelPendingSingleTap();
            var pivot = e.GetPosition(Root);
            if (_scale > _minScale * 1.15)
                SetScaleAt(_minScale, new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
            else
            {
                var target = Math.Min(MaxScale, Math.Max(_minScale * 2.5, 1.0));
                SetScaleAt(target, pivot);
            }
        }

        private void Viewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!_ready) return;
            if (_singleTapTimer == null)
            {
                _singleTapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
                _singleTapTimer.Tick += (s, args) =>
                {
                    _singleTapTimer.Stop();
                    ToggleChrome();
                };
            }
            _singleTapTimer.Stop();
            _singleTapTimer.Start();
        }

        private void CancelPendingSingleTap() => _singleTapTimer?.Stop();

        private void ToggleChrome()
        {
            _chromeVisible = !_chromeVisible;
            var vis = _chromeVisible ? Visibility.Visible : Visibility.Collapsed;
            TopChrome.Visibility = vis;
            BottomChrome.Visibility = vis;
            if (_items.Count > 1)
            {
                PrevButton.Visibility = vis;
                NextButton.Visibility = vis;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void FitButton_Click(object sender, RoutedEventArgs e) => FitToScreen(keepRelative: false);

        private void OneToOneButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            SetScaleAt(1.0, new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cachedStream == null && string.IsNullOrEmpty(_sourcePath))
            {
                ShowToast("没有可保存的图片");
                return;
            }

            try
            {
                SaveButton.IsEnabled = false;
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = "QQ_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                };
                picker.FileTypeChoices.Add("JPEG 图片", new List<string> { ".jpg" });
                picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    SaveButton.IsEnabled = true;
                    return;
                }

                await WriteImageToFileAsync(file);
                ShowToast("已保存");
            }
            catch (Exception ex)
            {
                ShowToast("保存失败: " + ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }

        private async Task WriteImageToFileAsync(StorageFile file)
        {
            if (_cachedStream != null)
            {
                _cachedStream.Seek(0);
                using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    outStream.Size = 0;
                    var ext = (file.FileType ?? ".jpg").ToLowerInvariant();
                    var decoder = await BitmapDecoder.CreateAsync(_cachedStream.CloneStream());
                    var encoderId = ext == ".png" ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;
                    var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
                    var pixels = await decoder.GetPixelDataAsync();
                    encoder.SetPixelData(
                        decoder.BitmapPixelFormat,
                        decoder.BitmapAlphaMode,
                        decoder.PixelWidth,
                        decoder.PixelHeight,
                        decoder.DpiX,
                        decoder.DpiY,
                        pixels.DetachPixelData());
                    await encoder.FlushAsync();
                }
                return;
            }

            if (_sourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using (var http = new HttpClient())
                {
                    var buffer = await http.GetBufferAsync(new Uri(_sourcePath));
                    await FileIO.WriteBufferAsync(file, buffer);
                }
            }
            else if (_sourcePath.StartsWith("ms-app", StringComparison.OrdinalIgnoreCase))
            {
                var src = await StorageFile.GetFileFromApplicationUriAsync(new Uri(_sourcePath));
                await src.CopyAndReplaceAsync(file);
            }
            else
            {
                var src = await StorageFile.GetFileFromPathAsync(_sourcePath);
                await src.CopyAndReplaceAsync(file);
            }
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                _toastTimer.Tick += (s, e) =>
                {
                    _toastTimer.Stop();
                    Toast.Visibility = Visibility.Collapsed;
                };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
