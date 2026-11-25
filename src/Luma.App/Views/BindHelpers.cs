using Luma.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Views;

/// <summary>
/// Static helpers for compiled bindings, e.g. <c>Visibility="{x:Bind v:BindHelpers.VisibleIfNotEmpty(ViewModel.Error), Mode=OneWay}"</c>.
/// (Plain <c>bool</c> → <see cref="Visibility"/> needs no helper: x:Bind converts it implicitly.)
/// </summary>
public static class BindHelpers
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotNull(object? value) => value is not null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNotEmpty(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfEmpty(string? value) => string.IsNullOrEmpty(value) ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool IsNotNull(object? value) => value is not null;

    public static bool IsNull(object? value) => value is null;

    public static bool IsNotNullOrEmpty(string? value) => !string.IsNullOrEmpty(value);

    public static bool IsNullOrEmpty(string? value) => string.IsNullOrEmpty(value);

    /// <summary>"#rrggbb" / "rrggbb" / "#aarrggbb" → brush for colour swatches; transparent when unparseable.</summary>
    public static Brush HexBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush(Colors.Transparent);
        try
        {
            return new SolidColorBrush(ThemeService.ParseColor(hex.StartsWith('#') ? hex : "#" + hex));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>Accent fill for a selected check circle, outline colour otherwise.</summary>
    public static Brush CheckBrush(bool isChecked) =>
        ThemeResources.GetBrush(isChecked ? "LumaAccentBrush" : "LumaBorderBrush") ?? new SolidColorBrush(Colors.Gray);

    /// <summary>Resolves an icon resource key such as "Icon.Compass" to its path data (for <c>LucideIcon.Data</c>).</summary>
    public static string? Icon(string? key) =>
        key is { Length: > 0 } && ThemeResources.TryGet(key, out var value) ? value as string : null;

    public static string FavoriteText(bool isFavorite) => isFavorite ? "Favorited" : "Favorite";
}

/// <summary>
/// Looks up app resources from code, honouring the theme Luma currently shows (ThemeService's preference, or the app's
/// theme when it follows Windows) so themed brushes resolve to the right dictionary.
/// </summary>
public static class ThemeResources
{
    public static bool TryGet(string key, out object? value)
    {
        value = null;
        if (Application.Current?.Resources is not { } root) return false;
        var themeKey = CurrentThemeKey();
        if (TryFind(root, key, themeKey, out value)) return true;
        return root.TryGetValue(key, out value);
    }

    public static Brush? GetBrush(string key) => TryGet(key, out var value) ? value as Brush : null;

    private static string CurrentThemeKey()
    {
        ElementTheme theme = ElementTheme.Default;
        try
        {
            theme = LumaServices.Get<ThemeService>().ElementTheme;
        }
        catch (InvalidOperationException)
        {
            // services not initialised (designer / tests)
        }
        if (theme == ElementTheme.Default)
            theme = Application.Current?.RequestedTheme == ApplicationTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        return theme == ElementTheme.Light ? "Light" : "Dark";
    }

    /// <summary>
    /// Theme dictionary for our theme first, then merged dictionaries last-to-first, then the dictionary's own lookup.
    /// (ResourceDictionary.TryGetValue would pick theme entries by the *application* theme, so it is only the fallback.)
    /// </summary>
    private static bool TryFind(ResourceDictionary dictionary, string key, string themeKey, out object? value)
    {
        if ((dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed) ||
             (themeKey == "Dark" && dictionary.ThemeDictionaries.TryGetValue("Default", out themed))) &&
            themed is ResourceDictionary themedDictionary && themedDictionary.TryGetValue(key, out value))
            return true;
        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (TryFind(dictionary.MergedDictionaries[i], key, themeKey, out value)) return true;
        }
        return dictionary.TryGetValue(key, out value);
    }
}
