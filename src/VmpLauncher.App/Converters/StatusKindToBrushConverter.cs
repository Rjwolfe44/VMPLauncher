using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VmpLauncher.App.Converters;

/// <summary>Maps a status kind string (ok | warn | error | info | neutral | checking) to a Space palette brush.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "ok" => "Space.Status.OnlineBrush",
            "warn" => "Space.Status.WarningBrush",
            "error" => "Space.Status.ErrorBrush",
            "info" or "checking" => "Space.Status.InfoBrush",
            _ => "Space.Status.OfflineBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value is true ? "Space.Status.OnlineBrush" : "Space.Status.WarningBrush") as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && value is not string { Length: 0 };
        if (Invert)
        {
            has = !has;
        }
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is string p && int.TryParse(p, out var target) && i == target ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
