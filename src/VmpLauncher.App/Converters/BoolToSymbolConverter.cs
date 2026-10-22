using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace VmpLauncher.App.Converters;

public sealed class BoolToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusKindToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "ok" => SymbolRegular.CheckmarkCircle24,
            "warn" => SymbolRegular.Warning24,
            "error" => SymbolRegular.ErrorCircle24,
            "checking" => SymbolRegular.ArrowSync24,
            _ => SymbolRegular.Circle24,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
