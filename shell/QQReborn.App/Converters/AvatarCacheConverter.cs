using System;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;
using QQReborn.App.Services;

namespace QQReborn.App.Converters
{
    /// <summary>
    /// Converts avatar path strings to cached BitmapImage instances via AvatarCache.
    /// Prevents ListView from re-decoding the same avatar on every scroll pass.
    /// </summary>
    public class AvatarCacheConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
                return AvatarCache.GetOrCreate(path);
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
