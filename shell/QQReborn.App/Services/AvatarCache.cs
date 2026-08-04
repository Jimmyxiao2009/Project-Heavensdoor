using System;
using System.Collections.Concurrent;
using Windows.UI.Xaml.Media.Imaging;

namespace QQReborn.App.Services
{
    /// <summary>
    /// In-memory cache for decoded BitmapImage instances (avatars, thumbnails).
    /// Prevents ListView from re-decoding the same image on every scroll pass.
    /// </summary>
    public static class AvatarCache
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.Ordinal);

        private const int MaxCacheSize = 200;

        /// <summary>
        /// Get or create a BitmapImage for the given path. Returns a cached instance
        /// when available, otherwise creates and caches a new one.
        /// </summary>
        public static BitmapImage GetOrCreate(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            if (_cache.TryGetValue(path, out var cached))
                return cached;

            // Trim cache if it grows too large (simple FIFO eviction).
            if (_cache.Count >= MaxCacheSize)
            {
                var toRemove = MaxCacheSize / 4; // evict 25%
                var count = 0;
                foreach (var key in _cache.Keys)
                {
                    if (count++ >= toRemove) break;
                    _cache.TryRemove(key, out _);
                }
            }

            var img = new BitmapImage();
            try
            {
                img.UriSource = new Uri(path);
                _cache[path] = img;
                return img;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Clear the entire cache (e.g., on account switch).</summary>
        public static void Clear()
        {
            _cache.Clear();
        }
    }
}
