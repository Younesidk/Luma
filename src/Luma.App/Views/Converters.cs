using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Luma.App.Views;

// IValueConverter versions of BindHelpers for {Binding} / template use. Instances are declared in Styles/Controls.xaml
// (keys: BoolToVisibility, InverseBoolToVisibility, NotNullToVisibility, NullToVisibility, NotEmptyToVisibility,
// EmptyToVisibility, InverseBool, HexBrush, CheckBrush, IconKey, FavoriteText). Prefer x:Bind + BindHelpers in views.

public abstract class OneWayConverter : IValueConverter
{
    public abstract object? Convert(object? value, Type targetType, object? parameter, string language);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();

    object IValueConverter.Convert(object value, Type targetType, object parameter, string language) => Convert(value, targetType, parameter, language)!;
}

/// <summary>true → Visible (or Collapsed with <see cref="Invert"/>).</summary>
public sealed class BoolToVisibilityConverter : OneWayConverter
{
    public bool Invert { get; set; }

    public override object? Convert(object? value, Type targetType, object? parameter, string language) =>
        BindHelpers.Visible((value is true) ^ Invert);
}

/// <summary>non-null → Visible (or null → Visible with <see cref="Invert"/>).</summary>
public sealed class NullToVisibilityConverter : OneWayConverter
{
    public bool Invert { get; set; }

    public override object? Convert(object? value, Type targetType, object? parameter, string language) =>
        BindHelpers.Visible((value is not null) ^ Invert);
}

/// <summary>non-empty string → Visible (or empty → Visible with <see cref="Invert"/>).</summary>
public sealed class StringToVisibilityConverter : OneWayConverter
{
    public bool Invert { get; set; }

    public override object? Convert(object? value, Type targetType, object? parameter, string language) =>
        BindHelpers.Visible(!string.IsNullOrEmpty(value as string) ^ Invert);
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}

/// <summary>Turns "#rrggbb" into a brush for colour swatches.</summary>
public sealed class HexBrushConverter : OneWayConverter
{
    public static HexBrushConverter Instance { get; } = new();

    public override object? Convert(object? value, Type targetType, object? parameter, string language) => BindHelpers.HexBrush(value as string);
}

/// <summary>Accent fill for a selected check circle, outline colour otherwise.</summary>
public sealed class CheckBrushConverter : OneWayConverter
{
    public static CheckBrushConverter Instance { get; } = new();

    public override object? Convert(object? value, Type targetType, object? parameter, string language) => BindHelpers.CheckBrush(value is true);
}

/// <summary>Resolves an icon resource key such as "Icon.Compass" to its path-data string.</summary>
public sealed class IconKeyConverter : OneWayConverter
{
    public static IconKeyConverter Instance { get; } = new();

    public override object? Convert(object? value, Type targetType, object? parameter, string language) => BindHelpers.Icon(value as string);
}

public sealed class FavoriteTextConverter : OneWayConverter
{
    public static FavoriteTextConverter Instance { get; } = new();

    public override object? Convert(object? value, Type targetType, object? parameter, string language) => BindHelpers.FavoriteText(value is true);
}
