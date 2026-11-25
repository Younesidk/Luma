using System.Collections.Specialized;
using System.ComponentModel;
using Luma.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Luma.App.Views;

/// <summary>
/// Discover: search, source selector, filters flyout, category chips and the gallery with its featured header.
/// </summary>
public sealed partial class DiscoverView : UserControl, ISearchFocusable
{
    private DiscoverViewModel? _viewModel;

    public DiscoverView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Internal: see WallpaperCard.ViewModel for why view models stay off public XAML surfaces.</summary>
    internal DiscoverViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(value, _viewModel)) return;
            Detach();
            _viewModel = value;
            Hero.ViewModel = value;
            Filters.ViewModel = value;
            CategoryList.ItemsSource = value?.Categories;
            Bindings.Update();
            if (IsLoaded) Attach();
        }
    }

    public bool FocusSearch() => ViewHelpers.FocusAndSelect(PageSearch);

    private bool _attached;

    private void Attach()
    {
        if (_attached || _viewModel is null) return;
        _attached = true;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Sources.CollectionChanged += OnSourcesChanged;
        RebuildSources();
    }

    private void Detach()
    {
        if (!_attached || _viewModel is null) return;
        _attached = false;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Sources.CollectionChanged -= OnSourcesChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoverViewModel.SelectedSource) or null or "") UpdateSourceChecks();
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildSources();

    private void RebuildSources()
    {
        SourceStrip.Children.Clear();
        if (_viewModel is null) return;
        var style = (Style)Application.Current.Resources["SegmentToggleButtonStyle"];
        foreach (var option in _viewModel.Sources)
        {
            var button = new ToggleButton
            {
                Style = style,
                Content = option.Label,
                Padding = new Thickness(13, 7, 13, 7),
                MinHeight = 0,
                CornerRadius = new CornerRadius(9),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Tag = option
            };
            AutomationProperties.SetName(button, option.Label);
            button.Click += OnSourceClick;
            SourceStrip.Children.Add(button);
        }
        UpdateSourceChecks();
    }

    private void UpdateSourceChecks()
    {
        var selected = _viewModel?.SelectedSource;
        foreach (var child in SourceStrip.Children)
        {
            if (child is ToggleButton { Tag: SourceOption option } button) button.IsChecked = Equals(option, selected);
        }
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is ToggleButton { Tag: SourceOption option })
        {
            if (!Equals(_viewModel.SelectedSource, option)) _viewModel.SelectedSource = option;
        }
        // Segments are not un-checkable: always reflect the current selection.
        UpdateSourceChecks();
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not ToggleButton { DataContext: CategoryChip chip } button) return;
        _viewModel.SelectCategoryCommand.Execute(chip);
        button.IsChecked = chip.IsActive;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _viewModel is null) return;
        e.Handled = true;
        _viewModel.SubmitSearchCommand.Execute(null);
    }
}
