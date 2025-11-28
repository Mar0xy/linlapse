using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace Linlapse.Converters;

/// <summary>
/// Converts a file path string to an Avalonia Bitmap for display in Image controls
/// </summary>
public class FilePathToBitmapConverter : IValueConverter
{
    public static readonly FilePathToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                if (File.Exists(path))
                {
                    return new Bitmap(path);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to load bitmap from {Path}: {Error}", path, ex.Message);
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
