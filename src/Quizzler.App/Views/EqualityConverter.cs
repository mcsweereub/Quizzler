using System.Globalization;
using System.Windows.Data;

namespace Quizzler.App.Views;

/// <summary>True when both bound values are equal; used for radio buttons bound to a "selected item" property.</summary>
public sealed class EqualityConverter : IMultiValueConverter
{
    public static EqualityConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
