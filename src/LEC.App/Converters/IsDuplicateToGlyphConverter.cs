// src/LEC.App/Converters/IsDuplicateToGlyphConverter.cs
using System.Globalization;
using System.Windows.Data;

namespace LEC.App.Converters;

// value is true cobre os 3 estados de bool? (true -> glifo; false/null -> vazio), no mesmo
// espírito de HasAttachmentToVisibilityConverter -- aqui a célula em si carrega o texto, não a
// visibilidade de um glifo fixo, porque IsDuplicate não tem um TextBlock estático no XAML.
public sealed class IsDuplicateToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "⧉" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
