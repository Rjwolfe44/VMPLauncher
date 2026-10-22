using System.Globalization;
using System.Windows.Data;

namespace VmpLauncher.App.Converters;

public sealed class PasswordPlaceholderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "•••••••• (saved) — type to replace" : "Enter the server password";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
