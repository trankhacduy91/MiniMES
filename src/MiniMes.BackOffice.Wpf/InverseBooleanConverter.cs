using System.Globalization;
using System.Windows.Data;

namespace MiniMes.BackOffice.Wpf;

// IsReadOnly="{Binding IsNew, Converter={StaticResource Not}}": a box editable only when creating.
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
