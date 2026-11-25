using System.ComponentModel;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Views;

/// <summary>Content of Discover's Filters flyout. Shows only the filter groups the selected source supports.</summary>
public sealed partial class FilterPanel : UserControl
{
    private readonly List<Action> _detach = [];
    private DiscoverViewModel? _viewModel;
    private Action? _detachSort;

    public FilterPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => FitToWindow();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal DiscoverViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            foreach (var detach in _detach) detach();
            _detach.Clear();
            _detachSort?.Invoke();
            _detachSort = null;
            _viewModel = value;
            ColorSwatches.ItemsSource = value?.Filters.Colors;
            if (value is not null) Wire(value);
            Bindings.Update();
        }
    }

    private void Wire(DiscoverViewModel vm)
    {
        var f = vm.Filters;
        // The sort list depends on the source, so it is re-wired whenever SortOptions changes.
        WireSort(vm);
        vm.PropertyChanged += OnViewModelChanged;
        _detach.Add(() => vm.PropertyChanged -= OnViewModelChanged);

        _detach.Add(ViewHelpers.BindCombo(TopRangeCombo, DiscoverFilters.TopRanges, o => o.Label, () => f.TopRangeOption, o => f.TopRangeOption = o, f, nameof(DiscoverFilters.TopRangeOption)));
        _detach.Add(ViewHelpers.BindCombo(ResolutionCombo, DiscoverFilters.Resolutions, o => o.Label, () => f.MinResolutionOption, o => f.MinResolutionOption = o, f, nameof(DiscoverFilters.MinResolutionOption)));
        _detach.Add(ViewHelpers.BindCombo(AspectCombo, DiscoverFilters.Aspects, o => o.Label, () => f.AspectOption, o => f.AspectOption = o, f, nameof(DiscoverFilters.AspectOption)));
        _detach.Add(ViewHelpers.BindCombo(ContentKindCombo, DiscoverFilters.ContentKinds, o => o.Label, () => f.ContentKindOption, o => f.ContentKindOption = o, f, nameof(DiscoverFilters.ContentKindOption)));
        _detach.Add(ViewHelpers.BindCombo(LicenseCombo, DiscoverFilters.Licenses, o => o.Label, () => f.LicenseOption, o => f.LicenseOption = o, f, nameof(DiscoverFilters.LicenseOption)));

        _detach.Add(ViewHelpers.BindToggle(GeneralToggle, () => f.General, v => f.General = v, f, nameof(DiscoverFilters.General)));
        _detach.Add(ViewHelpers.BindToggle(AnimeToggle, () => f.Anime, v => f.Anime = v, f, nameof(DiscoverFilters.Anime)));
        _detach.Add(ViewHelpers.BindToggle(PeopleToggle, () => f.People, v => f.People = v, f, nameof(DiscoverFilters.People)));
        _detach.Add(ViewHelpers.BindToggle(SafeToggle, () => f.Safe, v => f.Safe = v, f, nameof(DiscoverFilters.Safe)));
        _detach.Add(ViewHelpers.BindToggle(SketchyToggle, () => f.Sketchy, v => f.Sketchy = v, f, nameof(DiscoverFilters.Sketchy)));
        _detach.Add(ViewHelpers.BindToggle(ExplicitToggle, () => f.ExplicitContent, v => f.ExplicitContent = v, f, nameof(DiscoverFilters.ExplicitContent)));
    }

    private void WireSort(DiscoverViewModel vm)
    {
        _detachSort?.Invoke();
        var f = vm.Filters;
        _detachSort = ViewHelpers.BindCombo(SortCombo, vm.SortOptions, o => o.Label, () => f.SortOption, o => f.SortOption = o, f, nameof(DiscoverFilters.SortOption));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not null && e.PropertyName is nameof(DiscoverViewModel.SortOptions) or null or "") WireSort(_viewModel);
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && (sender as FrameworkElement)?.DataContext is ColorChoice choice)
            _viewModel.Filters.ToggleColorCommand.Execute(choice);
    }

    /// <summary>
    /// The flyout opens below the Filters button; on a short window a fixed 560px panel ran off the bottom of the screen.
    /// Cap it to the space the window actually has so it scrolls instead.
    /// </summary>
    private void FitToWindow()
    {
        if (XamlRoot is null || XamlRoot.Size.Height <= 0) return;
        Scroller.MaxHeight = Math.Clamp(XamlRoot.Size.Height - 200, 260, 560);
    }
}
