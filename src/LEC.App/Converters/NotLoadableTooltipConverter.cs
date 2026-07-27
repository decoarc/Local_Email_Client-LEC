// src/LEC.App/Converters/NotLoadableTooltipConverter.cs
using System.Globalization;
using System.Windows.Data;

namespace LEC.App.Converters;

public sealed class NotLoadableTooltipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? "Arquivo desta pasta não foi encontrado ou não pôde ser lido." : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
