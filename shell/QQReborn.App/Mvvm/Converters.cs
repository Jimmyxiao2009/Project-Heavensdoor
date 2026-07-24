using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace QQReborn.App.Mvvm
{
    /// <summary>true -> Visible, false -> Collapsed. Pass "invert" to flip.</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool b = value is bool v && v;
            if (parameter is string s && s == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is Visibility vis && vis == Visibility.Visible;
        }
    }

    /// <summary>true/false -> two brushes (e.g. pinned conversation row).</summary>
    public class BoolToBrushConverter : IValueConverter
    {
        public Brush TrueBrush { get; set; }
        public Brush FalseBrush { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool b = value is bool v && v;
            if (parameter is string s && s == "invert") b = !b;
            return b
                ? (TrueBrush ?? new SolidColorBrush(Windows.UI.Colors.Transparent))
                : (FalseBrush ?? new SolidColorBrush(Windows.UI.Colors.Transparent));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
