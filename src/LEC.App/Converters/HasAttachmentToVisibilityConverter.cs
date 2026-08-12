using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LEC.App.Converters;

// value is true cobre os 3 estados de bool? (true -> Visible; false/null -> Collapsed) sem
// lançar em null, ao contrário do BooleanToVisibilityConverter embutido do WPF (que não
// aceita bool?).
public sealed class HasAttachmentToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
