using System.Globalization;
using Luma.Core.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace Luma.App.Services;

public sealed record AccentPreset(string Id, string Name, Color Dark, Color Light);

/// <summary>
/// Applies the theme (system / dark / light) and the accent colour at runtime. The theme is set as
/// <see cref="FrameworkElement.RequestedTheme"/> on the window's root element; the accent is applied by mutating the
/// existing accent brushes in the app's (and WinUI's) theme dictionaries, so everything bound to them updates live.
/// </summary>
public sealed class ThemeService
{
    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("violet", "Violet", ParseColor("#8B7CFF"), ParseColor("#6C5CE7")),
        new("blue", "Azure", ParseColor("#6FA8FF"), ParseColor("#2F6FEB")),
        new("teal", "Lagoon", ParseColor("#4FD1C5"), ParseColor("#0E9488")),
        new("green", "Meadow", ParseColor("#78D58F"), ParseColor("#1F9D55")),
        new("amber", "Amber", ParseColor("#F5B84C"), ParseColor("#C26F00")),
        new("rose", "Rose", ParseColor("#F58AB4"), ParseColor("#D6336C"))
    ];

    private static readonly Color DarkOnAccent = ParseColor("#0F1120");
    private static readonly Color LightOnAccent = ParseColor("#FFFFFF");

    private FrameworkElement? _root;

    /// <summary>The selected theme preference (applied to every window root that attaches).</summary>
    public ThemePreference Theme { get; private set; } = ThemePreference.System;

    /// <summary>The id of the applied accent preset.</summary>
    public string AccentId { get; private set; } = "violet";

    /// <summary>The element theme matching <see cref="Theme"/>.</summary>
    public ElementTheme ElementTheme => Theme switch
    {
        ThemePreference.Dark => ElementTheme.Dark,
        ThemePreference.Light => ElementTheme.Light,
        _ => ElementTheme.Default
    };

    /// <summary>Raised after the theme or the accent changed (e.g. so the window can recolour its title bar).</summary>
    public event EventHandler? Changed;

    /// <summary>Sets (or clears, with null) the window root element that receives the requested theme.</summary>
    public void Attach(FrameworkElement? root)
    {
        _root = root;
        if (root is not null) root.RequestedTheme = ElementTheme;
    }

    public void Apply(ThemePreference theme, string accentId)
    {
        Theme = theme;
        if (_root is not null) _root.RequestedTheme = ElementTheme;
        ApplyAccent(accentId);
    }

    public void ApplyAccent(string accentId)
    {
        AccentId = Presets.Any(p => p.Id == accentId) ? accentId : "violet";
        var preset = Presets.First(p => p.Id == AccentId);

        if (Application.Current?.Resources is { } resources)
        {
            ApplyVariant(resources, "Dark", preset.Dark, 0.16, DarkOnAccent, dark: true);
            ApplyVariant(resources, "Default", preset.Dark, 0.16, DarkOnAccent, dark: true);
            ApplyVariant(resources, "Light", preset.Light, 0.12, LightOnAccent, dark: false);
            RefreshRoot();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyVariant(ResourceDictionary resources, string themeKey, Color accent, double subtleOpacity, Color onAccent, bool dark)
    {
        var light1 = Lighten(accent, 0.15);
        var light2 = Lighten(accent, 0.30);
        var light3 = Lighten(accent, 0.45);
        var dark1 = Lighten(accent, -0.12);
        var dark2 = Lighten(accent, -0.24);
        var dark3 = Lighten(accent, -0.36);
        var secondary = dark ? Lighten(accent, 0.22) : Lighten(accent, 0.18);

        var colors = new Dictionary<string, Color>
        {
            ["LumaAccentColor"] = accent,
            ["SystemAccentColor"] = accent,
            ["SystemAccentColorLight1"] = light1,
            ["SystemAccentColorLight2"] = light2,
            ["SystemAccentColorLight3"] = light3,
            ["SystemAccentColorDark1"] = dark1,
            ["SystemAccentColorDark2"] = dark2,
            ["SystemAccentColorDark3"] = dark3
        };

        // Luma's own tokens (created in the app dictionary when missing) plus WinUI's accent brushes (mutated where they exist).
        var lumaBrushes = new Dictionary<string, (Color Color, double Opacity)>
        {
            ["LumaAccentBrush"] = (accent, 1),
            ["LumaAccentSecondaryBrush"] = (secondary, 1),
            ["LumaAccentSubtleBrush"] = (accent, subtleOpacity),
            ["LumaOnAccentBrush"] = (onAccent, 1)
        };
        // Luma's dark presets are already light tints, so the accent itself is the fill in both themes.
        var fill = accent;
        var text = dark ? light3 : dark2;
        var winuiBrushes = new Dictionary<string, (Color Color, double Opacity)>
        {
            ["AccentFillColorDefaultBrush"] = (fill, 1),
            ["AccentFillColorSecondaryBrush"] = (fill, 0.9),
            ["AccentFillColorTertiaryBrush"] = (fill, 0.8),
            ["AccentFillColorSelectedTextBackgroundBrush"] = (accent, 1),
            ["AccentTextFillColorPrimaryBrush"] = (text, 1),
            ["AccentTextFillColorSecondaryBrush"] = (text, 1),
            ["AccentTextFillColorTertiaryBrush"] = (dark ? light2 : dark1, 1),
            ["TextOnAccentFillColorPrimaryBrush"] = (onAccent, 1),
            ["TextOnAccentFillColorSecondaryBrush"] = (onAccent, 0.7),
            ["SystemControlForegroundAccentBrush"] = (accent, 1),
            ["SystemControlHighlightAccentBrush"] = (accent, 1),
            ["SystemControlBackgroundAccentBrush"] = (accent, 1)
        };

        var own = GetOrCreateThemeDictionary(resources, themeKey, create: themeKey != "Default");
        if (own is not null)
        {
            foreach (var (key, color) in colors) own[key] = color;
            foreach (var (key, value) in lumaBrushes) SetBrush(own, key, value.Color, value.Opacity, createIfMissing: true);
        }

        foreach (var dictionary in ThemeDictionaries(resources, themeKey))
        {
            foreach (var (key, value) in lumaBrushes) SetBrush(dictionary, key, value.Color, value.Opacity, createIfMissing: false);
            foreach (var (key, value) in winuiBrushes) SetBrush(dictionary, key, value.Color, value.Opacity, createIfMissing: false);
        }
    }

    /// <summary>Mutates an existing brush instance (so live bindings update) or adds a new one.</summary>
    private static void SetBrush(ResourceDictionary dictionary, string key, Color color, double opacity, bool createIfMissing)
    {
        if (dictionary.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
            brush.Opacity = opacity;
        }
        else if (createIfMissing || dictionary.ContainsKey(key))
        {
            dictionary[key] = new SolidColorBrush(color) { Opacity = opacity };
        }
    }

    private static ResourceDictionary? GetOrCreateThemeDictionary(ResourceDictionary resources, string themeKey, bool create)
    {
        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var found) && found is ResourceDictionary dictionary) return dictionary;
        if (!create) return null;
        var created = new ResourceDictionary();
        resources.ThemeDictionaries[themeKey] = created;
        return created;
    }

    /// <summary>Every theme dictionary for <paramref name="themeKey"/> in the tree (the app's own and merged ones such as XamlControlsResources).</summary>
    private static IEnumerable<ResourceDictionary> ThemeDictionaries(ResourceDictionary root, string themeKey)
    {
        var stack = new Stack<ResourceDictionary>();
        var seen = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current)) continue;
            if (current.ThemeDictionaries.TryGetValue(themeKey, out var themed) && themed is ResourceDictionary t) yield return t;
            foreach (var merged in current.MergedDictionaries) stack.Push(merged);
        }
    }

    /// <summary>
    /// ThemeResource references resolved before a key was replaced only refresh on a theme change, so flip the root's theme
    /// once to make every control re-resolve its accent resources.
    /// </summary>
    private void RefreshRoot()
    {
        if (_root is null) return;
        var wanted = ElementTheme;
        var actual = _root.ActualTheme;
        _root.RequestedTheme = actual == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        _root.RequestedTheme = wanted;
    }

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
    public static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6) value = "FF" + value;
        if (value.Length != 8 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            throw new FormatException($"'{hex}' is not a colour.");
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private static Color Lighten(Color color, double amount)
    {
        static byte Mix(byte channel, double amount) =>
            (byte)Math.Clamp(amount >= 0 ? channel + (255 - channel) * amount : channel * (1 + amount), 0, 255);
        return Color.FromArgb(255, Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }
}
