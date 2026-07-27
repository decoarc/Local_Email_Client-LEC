// src/LEC.App/Converters/HealthToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LEC.DbxFormat;

namespace LEC.App.Converters;

public sealed class HealthToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StoreHealth.Ok or null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
