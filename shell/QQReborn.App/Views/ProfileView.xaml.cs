using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    /// <summary>
    /// The "我" (profile) area, embeddable into MainPage's 我 pivot item.
    /// Owns its own ProfileViewModel and a private profile service so it is
    /// self-contained; reads self identity from the shared App.ChatService.
    /// </summary>
    public sealed partial class ProfileView : UserControl
    {
        private readonly ProfileViewModel _vm;

        public ProfileView()
        {
            InitializeComponent();
            _vm = new ProfileViewModel(App.ChatService, new MockProfileService());
            DataContext = _vm;
            Loaded += ProfileView_Loaded;
            Unloaded += ProfileView_Unloaded;
        }

        private async void ProfileView_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.LoadAsync();

            // The status text binds to Self.StatusText, but the colored dot is a code-built
            // brush, so paint it now and refresh it whenever the status (color) changes.
            if (_vm.Self != null)
            {
                // Detach first so repeated Loaded/Unloaded cycles don't stack duplicate
                // subscriptions on the same long-lived SelfProfile (handler leak).
                _vm.Self.PropertyChanged -= Self_PropertyChanged;
                _vm.Self.PropertyChanged += Self_PropertyChanged;
                UpdateStatusDot();
            }
        }

        private void ProfileView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_vm.Self != null) _vm.Self.PropertyChanged -= Self_PropertyChanged;
        }

        private void Self_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelfProfile.StatusColorHex) ||
                e.PropertyName == nameof(SelfProfile.Status))
            {
                UpdateStatusDot();
            }
        }

        /// <summary>Repaints the status dot from the current SelfProfile.StatusColorHex.</summary>
        private void UpdateStatusDot()
        {
            if (_vm.Self == null) return;
            StatusDot.Fill = new SolidColorBrush(ParseColor(_vm.Self.StatusColorHex));
        }

        /// <summary>Parses a "#AARRGGBB" hex string into a Windows.UI.Color.</summary>
        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Colors.Gray;
            if (hex[0] == '#') hex = hex.Substring(1);
            // Expect AARRGGBB; fall back to a neutral grey if malformed.
            if (hex.Length != 8) return Colors.Gray;
            try
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            catch (FormatException)
            {
                return Colors.Gray;
            }
        }

        // ---- avatar upload (remote backend only) ----

        // Guards against a second tap re-entering the pick/scale/upload flow while one is
        // already in flight (picker dialog + upload round-trip can take a while).
        private bool _avatarBusy;
        private const uint MaxAvatarEdge = 640;

        private async void AvatarRect_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_avatarBusy) return;

            if (AppServices.Gateway == null)
            {
                // Mock backend: no real account to push an avatar to -- say so honestly
                // instead of silently doing nothing (which reads as a dead/broken tap target).
                await ShowMessageAsync("演示模式不支持更换头像。", "提示");
                return;
            }

            _avatarBusy = true;
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail, SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                string base64;
                try
                {
                    base64 = await EncodeScaledAvatarAsync(file);
                }
                catch (Exception)
                {
                    await ShowMessageAsync("头像更新失败", "提示");
                    return;
                }

                bool ok;
                try
                {
                    ok = await AppServices.Gateway.SetAvatarAsync(base64);
                }
                catch (Exception)
                {
                    ok = false;
                }

                await ShowMessageAsync(
                    ok ? "头像已更新（腾讯服务器生效可能有延迟）" : "头像更新失败",
                    "提示");

                // Reflect the picked image locally right away (best-effort cosmetic update --
                // the server is the source of truth and may take a moment to actually apply it).
                if (ok && _vm != null)
                {
                    try { _vm.SetLocalAvatarPreview(file.Path); } catch (Exception) { }
                }
            }
            finally
            {
                _avatarBusy = false;
            }
        }

        /// <summary>
        /// Decodes the picked image, downscales so its longer edge is at most
        /// <see cref="MaxAvatarEdge"/> px (server enforces a 1MB per-frame upload cap -- an
        /// original phone-camera photo sent as-is would blow well past that), re-encodes as
        /// JPEG at ~0.8 quality (regardless of the source format -- e.g. a picked .png -- so
        /// the server always gets the compact format the size budget assumes), and returns
        /// the result as a base64 string.
        /// </summary>
        private static async Task<string> EncodeScaledAvatarAsync(StorageFile file)
        {
            using (var inputStream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                double scale = 1.0;
                var longEdge = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                if (longEdge > MaxAvatarEdge) scale = (double)MaxAvatarEdge / longEdge;

                var targetWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                var targetHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = targetWidth,
                    ScaledHeight = targetHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
                var pixels = pixelData.DetachPixelData();

                using (var outputStream = new InMemoryRandomAccessStream())
                {
                    // Always encode as JPEG regardless of the source codec (e.g. a picked
                    // .png), and set the quality via the encoder's property set.
                    var propertySet = new BitmapPropertySet
                    {
                        ["ImageQuality"] = new BitmapTypedValue(0.8, Windows.Foundation.PropertyType.Single)
                    };
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, propertySet);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        targetWidth, targetHeight, decoder.DpiX, decoder.DpiY, pixels);
                    await encoder.FlushAsync();

                    var bytes = new byte[outputStream.Size];
                    outputStream.Seek(0);
                    using (var reader = new DataReader(outputStream.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)outputStream.Size);
                        reader.ReadBytes(bytes);
                    }
                    return Convert.ToBase64String(bytes);
                }
            }
        }

        private static async Task ShowMessageAsync(string message, string title)
        {
            try { await new MessageDialog(message, title).ShowAsync(); }
            catch (Exception) { }
        }

        private async void StatusRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            var remote = AppServices.Gateway;
            if (remote == null)
            {
                // Mock: local-only status flip still useful for UI demo.
                ShowLocalStatusPicker();
                return;
            }

            var combo = new ComboBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0,
            };
            // NapCat set_online_status: status / ext_status / battery_status
            combo.Items.Add(new ComboBoxItem { Content = "在线", Tag = OnlineStatus.Online });
            combo.Items.Add(new ComboBoxItem { Content = "离开", Tag = OnlineStatus.Away });
            combo.Items.Add(new ComboBoxItem { Content = "忙碌", Tag = OnlineStatus.Busy });
            combo.Items.Add(new ComboBoxItem { Content = "请勿打扰", Tag = OnlineStatus.DoNotDisturb });
            combo.Items.Add(new ComboBoxItem { Content = "隐身", Tag = OnlineStatus.Invisible });

            var dialog = new ContentDialog
            {
                Title = "设置在线状态",
                Content = combo,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            var status = (combo.SelectedItem as ComboBoxItem)?.Tag is OnlineStatus s ? s : OnlineStatus.Online;
            int st, ext, bat;
            MapOnlineStatus(status, out st, out ext, out bat);
            try
            {
                var ok = await remote.SetOnlineStatusAsync(st, ext, bat);
                if (ok)
                {
                    _vm.SetStatus(status);
                    UpdateStatusDot();
                    await ShowMessageAsync("状态已更新", "提示");
                }
                else await ShowMessageAsync("状态更新失败", "提示");
            }
            catch
            {
                await ShowMessageAsync("状态更新失败，请检查网络", "提示");
            }
        }

        private async void ShowLocalStatusPicker()
        {
            var combo = new ComboBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0,
            };
            combo.Items.Add(new ComboBoxItem { Content = "在线", Tag = OnlineStatus.Online });
            combo.Items.Add(new ComboBoxItem { Content = "离开", Tag = OnlineStatus.Away });
            combo.Items.Add(new ComboBoxItem { Content = "忙碌", Tag = OnlineStatus.Busy });
            combo.Items.Add(new ComboBoxItem { Content = "请勿打扰", Tag = OnlineStatus.DoNotDisturb });
            combo.Items.Add(new ComboBoxItem { Content = "隐身", Tag = OnlineStatus.Invisible });
            var dialog = new ContentDialog
            {
                Title = "设置在线状态（本地）",
                Content = combo,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                var status = (combo.SelectedItem as ComboBoxItem)?.Tag is OnlineStatus s ? s : OnlineStatus.Online;
                _vm.SetStatus(status);
                UpdateStatusDot();
            }
            catch { }
        }

        private static void MapOnlineStatus(OnlineStatus status, out int napcatStatus, out int ext, out int battery)
        {
            ext = 0;
            battery = 0;
            switch (status)
            {
                case OnlineStatus.Away: napcatStatus = 30; break;
                case OnlineStatus.Busy: napcatStatus = 50; break;
                case OnlineStatus.DoNotDisturb: napcatStatus = 70; break;
                case OnlineStatus.Invisible: napcatStatus = 40; break;
                default: napcatStatus = 10; break;
            }
        }

        private async void Nickname_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await EditProfileFieldAsync(editNickname: true);
        }

        private async void Signature_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await EditProfileFieldAsync(editNickname: false);
        }

        private async Task EditProfileFieldAsync(bool editNickname)
        {
            var remote = AppServices.Gateway;
            if (remote == null)
            {
                await ShowMessageAsync("演示模式不支持修改资料。", "提示");
                return;
            }

            var input = new TextBox
            {
                Text = editNickname ? (_vm.Nickname ?? "") : (_vm.Signature ?? ""),
                Margin = new Thickness(0, 12, 0, 0),
            };
            var dialog = new ContentDialog
            {
                Title = editNickname ? "修改昵称" : "修改个性签名",
                Content = input,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
            };

            ContentDialogResult res;
            try { res = await dialog.ShowAsync(); }
            catch { return; }
            if (res != ContentDialogResult.Primary) return;

            var text = (input.Text ?? "").Trim();
            try
            {
                bool ok;
                if (editNickname)
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        await ShowMessageAsync("昵称不能为空", "提示");
                        return;
                    }
                    ok = await remote.SetSelfProfileAsync(nickname: text, signature: null);
                    if (ok) _vm.ApplyLocalNickname(text);
                }
                else
                {
                    ok = await remote.SetSelfProfileAsync(nickname: null, signature: text);
                    if (ok) _vm.ApplyLocalSignature(text);
                }
                await ShowMessageAsync(ok ? "已保存" : "保存失败", "提示");
            }
            catch
            {
                await ShowMessageAsync("保存失败，请检查网络", "提示");
            }
        }

        private void MenuRow_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            switch (tag)
            {
                case "settings":
                    Navigate(typeof(SettingsPage));
                    break;
                case "favorites":
                case "album":
                case "files":
                case "dressup":
                    Navigate(typeof(ProfilePlaceholderPage), tag);
                    break;
            }
        }

        private void Navigate(System.Type pageType, object parameter = null)
        {
            // A UserControl has no Frame of its own; use the app's root frame
            // (may sit inside UiScaleService's host grid).
            UiScaleService.GetRootFrame()?.Navigate(pageType, parameter);
        }
    }
}
