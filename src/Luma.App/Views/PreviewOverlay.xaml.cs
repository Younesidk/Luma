using System.ComponentModel;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Views;

/// <summary>
/// Full-window wallpaper preview: the image stage (click the backdrop to close, previous/next buttons) and the details
/// sidebar (apply with fit mode, download, favourite, collection, source, facts, palette, tags, license).
/// </summary>
public sealed partial class PreviewOverlay : UserControl
{
    private PreviewViewModel? _viewModel;
    private Action? _detachFit;

    public PreviewOverlay()
    {
        InitializeComponent();
        Stage.PointerPressed += OnStagePressed;
        CloseButton.SizeChanged += (_, _) => TopBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) =>
        {
            if (XamlRoot is not null) XamlRoot.Changed += OnXamlRootChanged;
            UpdateImageCap();
        };
        Unloaded += (_, _) =>
        {
            if (XamlRoot is not null) XamlRoot.Changed -= OnXamlRootChanged;
        };
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateImageCap();

    /// <summary>
    /// WinUI has no <c>StretchDirection.DownOnly</c>: cap the image at its real pixel size (in DIPs for the current DPI)
    /// so a small wallpaper is shown sharp at 1:1 instead of blown up. The thumbnail placeholder is left uncapped.
    /// </summary>
    private void UpdateImageCap()
    {
        var vm = _viewModel;
        var scale = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1.0;
        if (vm is { ImagePixelWidth: > 0, ImagePixelHeight: > 0 })
        {
            StageImage.MaxWidth = vm.ImagePixelWidth / scale;
            StageImage.MaxHeight = vm.ImagePixelHeight / scale;
        }
        else
        {
            StageImage.MaxWidth = double.PositiveInfinity;
            StageImage.MaxHeight = double.PositiveInfinity;
        }
    }

    /// <summary>
    /// The preview's view model. Internal on purpose: a public property would make the XAML compiler emit type metadata
    /// for its Core model (<c>Wallpaper</c>, which has required members).
    /// </summary>
    internal PreviewViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _detachFit?.Invoke();
            _detachFit = null;
            _viewModel = value;
            if (value is not null)
            {
                value.PropertyChanged += OnViewModelChanged;
                _detachFit = ViewHelpers.BindCombo(FitCombo, value.FitModes, m => m.ToString(), () => value.SelectedFit,
                    m => value.SelectedFit = m, value, nameof(PreviewViewModel.SelectedFit));
            }
            else
            {
                FitCombo.ItemsSource = null;
            }
            Bindings.Update();
            RebuildPalette();
            RebuildTags();
            UpdateImageCap();
        }
    }

    /// <summary>Interactive elements of the top bar, which overlaps the window's drag strip.</summary>
    internal IEnumerable<FrameworkElement> TopBarElements => [CloseButton];

    /// <summary>Raised when the top-bar elements may have moved (MainWindow refreshes the pass-through regions).</summary>
    internal event EventHandler? TopBarLayoutChanged;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.Colors):
                RebuildPalette();
                break;
            case nameof(PreviewViewModel.Tags):
                RebuildTags();
                break;
            case nameof(PreviewViewModel.ImagePixelWidth):
            case nameof(PreviewViewModel.ImagePixelHeight):
                UpdateImageCap();
                break;
            case nameof(PreviewViewModel.Details):
            case nameof(PreviewViewModel.Item):
            case null:
            case "":
                RebuildPalette();
                RebuildTags();
                break;
        }
    }

    /// <summary>A click on the empty area around the image closes the preview.</summary>
    private void OnStagePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || !ReferenceEquals(e.OriginalSource, Stage)) return;
        if (!e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed) return;
        _viewModel.CloseCommand.Execute(null);
        e.Handled = true;
    }

    private void RebuildPalette()
    {
        PalettePanel.Children.Clear();
        if (_viewModel is null) return;
        foreach (var hex in _viewModel.Colors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = BindHelpers.HexBrush(hex),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };
            ToolTipService.SetToolTip(swatch, hex);
            PalettePanel.Children.Add(swatch);
        }
    }

    private void RebuildTags()
    {
        TagsPanel.Children.Clear();
        if (_viewModel is null) return;
        var pill = (Style)Application.Current.Resources["PillBorderStyle"];
        foreach (var tag in _viewModel.Tags)
        {
            TagsPanel.Children.Add(new Border
            {
                Style = pill,
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = tag, Style = (Style)Resources["TagTextStyle"] }
            });
        }
    }
}
