using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Luma.App.Views;

/// <summary>Accent fill for a selected check circle, outline colour otherwise.</summary>
public sealed class CheckBrushConverter : IValueConverter
{
    public static CheckBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "LumaAccentBrush" : "LumaBorderBrush";
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) ? brush as IBrush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Turns "#rrggbb" into a brush for colour swatches.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static HexBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex.StartsWith('#') ? hex : "#" + hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

