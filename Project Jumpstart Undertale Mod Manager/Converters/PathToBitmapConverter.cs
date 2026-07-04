using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Project_Jumpstart_Undertale_Mod_Manager.Converters;

public class PathToBitmapConverter : IValueConverter
{
    // A static instance so we don't have to declare it in XAML resources
    public static PathToBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && File.Exists(path))
        {
            try
            {
                // Loads the local file into an Avalonia Bitmap
                return new Bitmap(path);
            }
            catch
            {
                // Fails silently if the image is corrupted or locked
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}