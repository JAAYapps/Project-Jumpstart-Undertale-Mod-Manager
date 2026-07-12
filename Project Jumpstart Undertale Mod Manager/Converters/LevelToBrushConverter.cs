using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Project_Jumpstart_Undertale_Mod_Manager.Models;

namespace Project_Jumpstart_Undertale_Mod_Manager.Converters;

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        LogLevel.Error => Brushes.IndianRed,
        LogLevel.Warn  => Brushes.Goldenrod,
        _              => Brushes.Gainsboro,
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}