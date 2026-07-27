// src/LEC.App/Converters/LoadableToBrushConverter.cs
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LEC.App.Converters;

public sealed class LoadableToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.Black : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
