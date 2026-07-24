using System;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Data;

namespace QQReborn.App.Mvvm
{
    /// <summary>
    /// true -> QQ accent blue, false -> grey secondary. Used to tint the ♥ like
    /// glyph/count in a moment footer when the user has liked it. Defined here (not in
    /// App.xaml) and declared locally in MomentsView so it stays self-contained.
    /// </summary>
    public class LikedToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Accent =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0xB7, 0xF5));
        private static readonly SolidColorBrush Secondary =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool liked = value is bool b && b;
            return liked ? Accent : Secondary;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return false;
        }
    }
}
