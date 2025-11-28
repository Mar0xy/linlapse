using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace Linlapse.Converters;

/// <summary>
/// Converts a file path string to an Avalonia Bitmap for display in Image controls.
/// Caches bitmaps to avoid memory leaks from repeated conversions.
/// </summary>
public class FilePathToBitmapConverter : IValueConverter
{
    public static readonly FilePathToBitmapConverter Instance = new();
    
    // Cache bitmaps by path to avoid creating duplicates
    private readonly Dictionary<string, WeakReference<Bitmap>> _cache = new();
    private readonly object _cacheLock = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                if (File.Exists(path))
                {
                    lock (_cacheLock)
                    {
                        // Check if we have a cached bitmap that's still alive
                        if (_cache.TryGetValue(path, out var weakRef) && weakRef.TryGetTarget(out var cachedBitmap))
                        {
                            return cachedBitmap;
                        }
                        
                        // Create new bitmap and cache it
                        var bitmap = new Bitmap(path);
                        _cache[path] = new WeakReference<Bitmap>(bitmap);
                        
                        // Clean up dead references periodically
                        if (_cache.Count > 50)
                        {
                            CleanupCache();
                        }
                        
                        return bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to load bitmap from {Path}: {Error}", path, ex.Message);
            }
        }
        return null;
    }

    private void CleanupCache()
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in _cache)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in keysToRemove)
        {
            _cache.Remove(key);
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
