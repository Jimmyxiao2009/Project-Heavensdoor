using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace QQReborn.App.Services
{
    /// <summary>
    /// App-wide UI density (设置 · 界面大小).
    ///
    /// Do NOT scale the root Frame with RenderTransform. On UWP Mobile that caused:
    /// 1) white right/bottom bars (Window.Content ignores Width/Height), and
    /// 2) UI-thread freezes when navigating to MainPage (Pivot measure thrash under scale).
    ///
    /// Bind FontSize / row Height / avatar size to this object instead — layout reflows.
    /// Instantiated once from App.xaml resources as key "UiScale".
    /// </summary>
    public sealed class UiScaleService : INotifyPropertyChanged
    {
        public const string KeyFontSizeLevel = "qqr.settings.fontSizeLevel";

        /// <summary>The singleton created from App.xaml (or a fallback if XAML not ready).</summary>
        public static UiScaleService Current { get; private set; }

        private int _level = 1;
        private double _factor = 1.0;

        public UiScaleService()
        {
            // App.xaml creates the resource instance; keep a static handle for code-behind.
            if (Current == null) Current = this;
            ApplyLevelCore(LoadLevel(), persist: false, raise: false);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public static event EventHandler Changed;

        public int Level => _level;
        public double Factor => _factor;

        // Design tokens at factor=1 ("标准"). Phone-tuned smaller than classic WP chrome.
        public double PageTitle => S(13);
        public double PanoramaTitle => S(52);
        public double HeroTitle => S(34);
        public double SectionLabel => S(13);
        public double Body => S(17);
        public double Caption => S(12);
        public double PivotHeader => S(34);
        public double PivotHeaderHeight => S(60);
        public double ListTitle => S(17);
        public double ListPreview => S(12);
        public double ListTime => S(11);
        public double ConversationRowHeight => S(70);
        public double ConversationAvatar => S(50);
        public double ContactRowHeight => S(64);
        public double ContactAvatar => S(46);
        public double BubbleText => S(15);
        public double BubbleMeta => S(11);
        public double MenuRow => S(19);
        public double SettingsLabel => S(18);
        public double SettingsHint => S(12);
        public double UnreadBadge => S(11);
        public double BackGlyph => S(20);

        private double S(double design) => Math.Round(design * _factor, 1);

        public static double ScaleForLevel(int level)
        {
            if (level <= 0) return 0.88;
            if (level >= 2) return 1.08;
            return 1.0;
        }

        public static string LabelForLevel(int level)
        {
            if (level <= 0) return "小";
            if (level >= 2) return "大";
            return "标准";
        }

        public static int LoadLevel()
        {
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[KeyFontSizeLevel];
                if (raw is int i) return Clamp(i);
                if (raw is long l) return Clamp((int)l);
                if (raw is double d) return Clamp((int)Math.Round(d));
            }
            catch { }
            return 1;
        }

        public static void ApplyFromSettings()
        {
            EnsureCurrent().ApplyLevelCore(LoadLevel(), persist: false, raise: true);
        }

        public static void ApplyLevel(int level, bool persist = true)
        {
            EnsureCurrent().ApplyLevelCore(level, persist, raise: true);
        }

        public static Frame GetRootFrame()
        {
            return Window.Current?.Content as Frame;
        }

        /// <summary>Legacy no-op API (host grid removed).</summary>
        public static Frame EnsureHostedFrame(Frame frame = null)
        {
            if (frame != null && Window.Current != null && Window.Current.Content == null)
                Window.Current.Content = frame;
            return GetRootFrame() ?? frame;
        }

        private static UiScaleService EnsureCurrent()
        {
            if (Current != null) return Current;
            // Fallback before App resources exist (should be rare).
            return new UiScaleService();
        }

        private void ApplyLevelCore(int level, bool persist, bool raise)
        {
            level = Clamp(level);
            var factor = ScaleForLevel(level);
            var changed = level != _level || Math.Abs(factor - _factor) > 0.0001;
            _level = level;
            _factor = factor;

            if (persist)
            {
                try { ApplicationData.Current.LocalSettings.Values[KeyFontSizeLevel] = level; }
                catch { }
            }

            if (!changed) return;

            RaiseAll();
            if (raise)
            {
                try { Changed?.Invoke(null, EventArgs.Empty); }
                catch { }
            }
        }

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(Factor));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PanoramaTitle));
            OnPropertyChanged(nameof(HeroTitle));
            OnPropertyChanged(nameof(SectionLabel));
            OnPropertyChanged(nameof(Body));
            OnPropertyChanged(nameof(Caption));
            OnPropertyChanged(nameof(PivotHeader));
            OnPropertyChanged(nameof(PivotHeaderHeight));
            OnPropertyChanged(nameof(ListTitle));
            OnPropertyChanged(nameof(ListPreview));
            OnPropertyChanged(nameof(ListTime));
            OnPropertyChanged(nameof(ConversationRowHeight));
            OnPropertyChanged(nameof(ConversationAvatar));
            OnPropertyChanged(nameof(ContactRowHeight));
            OnPropertyChanged(nameof(ContactAvatar));
            OnPropertyChanged(nameof(BubbleText));
            OnPropertyChanged(nameof(BubbleMeta));
            OnPropertyChanged(nameof(MenuRow));
            OnPropertyChanged(nameof(SettingsLabel));
            OnPropertyChanged(nameof(SettingsHint));
            OnPropertyChanged(nameof(UnreadBadge));
            OnPropertyChanged(nameof(BackGlyph));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
            catch { }
        }

        private static int Clamp(int level)
        {
            if (level < 0) return 0;
            if (level > 2) return 2;
            return level;
        }
    }
}
