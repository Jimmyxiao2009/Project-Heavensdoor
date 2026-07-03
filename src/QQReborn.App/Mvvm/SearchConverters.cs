using System;
using Windows.UI.Xaml.Data;
using QQReborn.App.Models;

namespace QQReborn.App.Mvvm
{
    /// <summary>
    /// Maps a <see cref="SearchResultKind"/> to a small plain-text tag shown to the
    /// right of a result row (avoids Segoe MDL2 glyphs which tofu on the target).
    /// </summary>
    public class SearchKindToTagConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is SearchResultKind kind)
            {
                switch (kind)
                {
                    case SearchResultKind.Contact: return "好友";
                    case SearchResultKind.Group: return "群";
                    case SearchResultKind.Message: return "记录";
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
