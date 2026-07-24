using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace QQReborn.App.Mvvm
{
    /// <summary>群主 -> accent brush; 管理员 / others -> secondary brush.</summary>
    public class RoleToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var role = value as string;
            var key = role == "群主" ? "MetroAccentBrush" : "MetroSecondaryTextBrush";
            return Application.Current.Resources[key] as Brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
