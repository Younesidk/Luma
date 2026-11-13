using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Luma.Core.Enums;

namespace Luma.App.Services;

public sealed record AccentPreset(string Id, string Name, Color Dark, Color Light);

/// <summary>Applies the theme variant (system / dark / light) and the accent colour at runtime.</summary>
public sealed class ThemeService
{
    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("violet", "Violet", Color.Parse("#8B7CFF"), Color.Parse("#6C5CE7")),
        new("blue", "Azure", Color.Parse("#6FA8FF"), Color.Parse("#2F6FEB")),
        new("teal", "Lagoon", Color.Parse("#4FD1C5"), Color.Parse("#0E9488")),
        new("green", "Meadow", Color.Parse("#78D58F"), Color.Parse("#1F9D55")),
        new("amber", "Amber", Color.Parse("#F5B84C"), Color.Parse("#C26F00")),
        new("rose", "Rose", Color.Parse("#F58AB4"), Color.Parse("#D6336C"))
    ];

    private string _accentId = "violet";

    public void Apply(ThemePreference theme, string accentId)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = theme switch
        {
            ThemePreference.Dark => ThemeVariant.Dark,
            ThemePreference.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
        ApplyAccent(accentId);
    }

    public void ApplyAccent(string accentId)
    {
        var app = Application.Current;
        if (app is null) return;
        _accentId = Presets.Any(p => p.Id == accentId) ? accentId : "violet";
        var preset = Presets.First(p => p.Id == _accentId);

        Apply(app, ThemeVariant.Dark, preset.Dark, 0.16, Color.Parse("#0F1120"));
        Apply(app, ThemeVariant.Light, preset.Light, 0.12, Colors.White);
    }

    private static void Apply(Application app, ThemeVariant variant, Color accent, double subtleOpacity, Color onAccent)
    {
        var dict = FindThemeDictionary(app.Resources, variant);
        if (dict is null) return;

        var secondary = variant == ThemeVariant.Dark ? Lighten(accent, 0.22) : Lighten(accent, 0.18);
        dict["LumaAccentColor"] = accent;
        dict["LumaAccentBrush"] = new SolidColorBrush(accent);
        dict["LumaAccentSecondaryBrush"] = new SolidColorBrush(secondary);
        dict["LumaAccentSubtleBrush"] = new SolidColorBrush(accent, subtleOpacity);
        dict["LumaOnAccentBrush"] = new SolidColorBrush(onAccent);
        dict["TextControlBorderBrushFocused"] = new SolidColorBrush(accent);
        dict["ComboBoxBorderBrushPressed"] = new SolidColorBrush(accent);
        dict["ListBoxItemBackgroundSelected"] = new SolidColorBrush(accent, subtleOpacity + 0.04);
        dict["ListBoxItemBackgroundSelectedPointerOver"] = new SolidColorBrush(accent, subtleOpacity + 0.10);
        dict["ToggleSwitchKnobFillOn"] = new SolidColorBrush(onAccent);
        dict["SystemAccentColor"] = accent;
        dict["SystemAccentColorLight1"] = Lighten(accent, 0.15);
        dict["SystemAccentColorLight2"] = Lighten(accent, 0.30);
        dict["SystemAccentColorLight3"] = Lighten(accent, 0.45);
        dict["SystemAccentColorDark1"] = Lighten(accent, -0.12);
        dict["SystemAccentColorDark2"] = Lighten(accent, -0.24);
        dict["SystemAccentColorDark3"] = Lighten(accent, -0.36);
    }

    /// <summary>The token dictionaries are merged in via ResourceInclude, so look inside the merged dictionaries as well.</summary>
    private static ResourceDictionary? FindThemeDictionary(IResourceDictionary root, ThemeVariant variant)
    {
        if (root.ThemeDictionaries.TryGetValue(variant, out var direct) && direct is ResourceDictionary d) return d;
        foreach (var merged in root.MergedDictionaries)
        {
            var candidate = merged switch
            {
                ResourceInclude include => include.Loaded,
                ResourceDictionary dictionary => dictionary,
                _ => null
            };
            if (candidate is null) continue;
            var found = FindThemeDictionary(candidate, variant);
            if (found is not null) return found;
        }
        return null;
    }

    private static Color Lighten(Color color, double amount)
    {
        static byte Mix(byte channel, double amount) =>
            (byte)Math.Clamp(amount >= 0 ? channel + (255 - channel) * amount : channel * (1 + amount), 0, 255);
        return Color.FromRgb(Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }
}


