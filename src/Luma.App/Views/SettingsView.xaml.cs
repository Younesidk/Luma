using System.ComponentModel;
using Luma.App.Services;
using Luma.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Views;

/// <summary>
/// Settings: appearance (theme, six accent swatches, density, sidebar), downloads, providers and keys, wallpaper and
/// rotation, live-wallpaper policies, performance, and About with the Updates row. ComboBoxes over option records,
/// int sliders and the swatches are wired here because compiled bindings can't express them.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;
    private readonly List<Action> _detach = [];
    private readonly List<(AccentSwatchViewModel Swatch, Button Button)> _swatches = [];

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        ActualThemeChanged += (_, _) => UpdateSwatches();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal SettingsViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            Detach();
            _viewModel = value;
            Bindings.Update();
            if (IsLoaded) Attach();
        }
    }

    public string VersionText(string? version) => $"Version {version}";

    private void Attach()
    {
        if (_detach.Count > 0 || _viewModel is not { } vm) return;

        _detach.Add(ViewHelpers.BindCombo(ThemeCombo, vm.ThemeOptions, o => o.Label, () => vm.SelectedTheme, v => vm.SelectedTheme = v,
            vm, nameof(SettingsViewModel.SelectedTheme)));
        _detach.Add(ViewHelpers.BindCombo(DensityCombo, vm.DensityOptions, o => o.Label, () => vm.SelectedDensity, v => vm.SelectedDensity = v,
            vm, nameof(SettingsViewModel.SelectedDensity)));
        _detach.Add(ViewHelpers.BindCombo(FitCombo, vm.FitOptions, o => o.Label, () => vm.SelectedFit, v => vm.SelectedFit = v,
            vm, nameof(SettingsViewModel.SelectedFit)));
        _detach.Add(ViewHelpers.BindComboLive(SourceCombo, vm.SourceOptions, o => o.Label, () => vm.SelectedDefaultSource, v => vm.SelectedDefaultSource = v,
            vm, nameof(SettingsViewModel.SelectedDefaultSource)));
        _detach.Add(ViewHelpers.BindCombo(IntervalCombo, vm.IntervalOptions, o => o.Label, () => vm.SelectedInterval, v => vm.SelectedInterval = v,
            vm, nameof(SettingsViewModel.SelectedInterval)));
        _detach.Add(ViewHelpers.BindCombo(RotationSourceCombo, vm.RotationSourceOptions, o => o.Label, () => vm.SelectedRotationSource,
            v => vm.SelectedRotationSource = v, vm, nameof(SettingsViewModel.SelectedRotationSource)));
        _detach.Add(ViewHelpers.BindComboLive(RotationCollectionCombo, vm.CollectionOptions, o => o.Label, () => vm.SelectedRotationCollection!,
            v => vm.SelectedRotationCollection = v, vm, nameof(SettingsViewModel.SelectedRotationCollection)));

        _detach.Add(ViewHelpers.BindSlider(ThumbnailCacheSlider, () => vm.ThumbnailCacheMegabytes, v => vm.ThumbnailCacheMegabytes = v,
            vm, nameof(SettingsViewModel.ThumbnailCacheMegabytes)));
        _detach.Add(ViewHelpers.BindSlider(MemoryCacheSlider, () => vm.MemoryCacheItems, v => vm.MemoryCacheItems = v,
            vm, nameof(SettingsViewModel.MemoryCacheItems)));
        _detach.Add(ViewHelpers.BindSlider(DownloadsSlider, () => vm.MaxConcurrentDownloads, v => vm.MaxConcurrentDownloads = v,
            vm, nameof(SettingsViewModel.MaxConcurrentDownloads)));

        BuildSwatches(vm);
    }

    private void Detach()
    {
        foreach (var detach in _detach) detach();
        _detach.Clear();
        foreach (var (swatch, _) in _swatches) swatch.PropertyChanged -= OnSwatchChanged;
        _swatches.Clear();
        AccentStrip.Children.Clear();
    }

    // ------------------------------------------------------------------ Accent swatches

    private void BuildSwatches(SettingsViewModel vm)
    {
        var style = (Style)Resources["SwatchButtonStyle"];
        foreach (var swatch in vm.Accents)
        {
            var button = new Button { Style = style, Background = swatch.Brush, Command = swatch.SelectCommand };
            ToolTipService.SetToolTip(button, swatch.Name);
            AutomationProperties.SetName(button, swatch.Name);
            button.PointerEntered += (_, _) => UpdateSwatch(swatch, button, hover: true);
            button.PointerExited += (_, _) => UpdateSwatch(swatch, button, hover: false);
            button.PointerCanceled += (_, _) => UpdateSwatch(swatch, button, hover: false);
            swatch.PropertyChanged += OnSwatchChanged;
            _swatches.Add((swatch, button));
            AccentStrip.Children.Add(button);
        }
        UpdateSwatches();
    }

    private void OnSwatchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccentSwatchViewModel.IsSelected) or null or "") UpdateSwatches();
    }

    private void UpdateSwatches()
    {
        foreach (var (swatch, button) in _swatches) UpdateSwatch(swatch, button, hover: false);
    }

    /// <summary>Selected: primary-text ring. Hovered: tertiary ring. Otherwise no ring.</summary>
    private static void UpdateSwatch(AccentSwatchViewModel swatch, Button button, bool hover)
    {
        var key = swatch.IsSelected ? "LumaTextPrimaryBrush" : hover ? "LumaTextTertiaryBrush" : null;
        button.BorderBrush = key is null ? new SolidColorBrush(Colors.Transparent) : ThemeResources.GetBrush(key) ?? new SolidColorBrush(Colors.Transparent);
    }
}
